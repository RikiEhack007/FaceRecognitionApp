using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using OpenCvSharp;
using FaceRecApp.Core.Entities;

namespace FaceRecApp.Core.Services;

/// <summary>
/// Manages webcam capture using OpenCvSharp.
///
/// Architecture:
///   - Runs a continuous capture loop on a background thread
///   - Fires FrameCaptured event for every new frame (~30fps)
///   - The UI subscribes to display the live feed
///   - The RecognitionPipeline subscribes to process frames for faces
///
/// Thread model:
///   - Capture runs on its own dedicated thread
///   - Events are fired on the capture thread (NOT the UI thread)
///   - Subscribers must use Dispatcher.Invoke() for UI updates
///
/// Lifecycle:
///   1. new CameraService() → constructor (lightweight)
///   2. StartAsync()        → opens webcam + starts capture loop
///   3. FrameCaptured event → fires continuously
///   4. StopAsync()         → stops loop + releases webcam
///   5. Dispose()           → cleanup
/// </summary>
public class CameraService : IDisposable
{
    private VideoCapture? _capture;
    private Thread? _captureThread;
    private volatile bool _isRunning;
    private bool _disposed;
    private int _frameCount;
    private DateTime _fpsTimer;
    private readonly object _lock = new();

    // ─── Events ───

    /// <summary>
    /// Fired for every captured frame. Subscribers receive the raw Mat.
    ///
    /// WARNING: This fires on the capture thread, not the UI thread.
    /// For WPF display, use Dispatcher.Invoke() or convert to frozen BitmapSource.
    ///
    /// The Mat is reused between frames — if you need to keep it,
    /// call mat.Clone() before the event handler returns.
    /// </summary>
    public event EventHandler<FrameEventArgs>? FrameCaptured;

    /// <summary>
    /// Fired when the camera encounters an error.
    /// </summary>
    public event EventHandler<string>? CameraError;

    // ─── Properties ───

    /// <summary>
    /// Is the camera currently capturing?
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Current frames per second.
    /// </summary>
    public double CurrentFps { get; private set; }

    /// <summary>
    /// Total frames captured since start.
    /// </summary>
    public long TotalFrames { get; private set; }

    // ─── Configuration Properties ───

    /// <summary>
    /// Maximum number of OpenCV device indices to probe (default 10).
    /// </summary>
    public int MaxProbeDevices { get; set; } = 10;

    /// <summary>
    /// Preferred camera device name. If set, AutoSelectDevice will prioritize
    /// devices whose name contains this string (case-insensitive).
    /// </summary>
    public string PreferredDeviceName { get; set; } = "";

    /// <summary>
    /// When true, AutoSelectDevice prefers phone/virtual cameras over physical ones.
    /// </summary>
    public bool PreferPhoneCamera { get; set; }

    // ─── Phone/Virtual Camera Detection Patterns ───

    private static readonly string[] PhoneCameraPatterns =
    [
        "phone link", "link to windows", "windows virtual camera",
        "cross device", "droidcam", "iriun", "epoccam", "camo",
        "obs virtual", "virtual camera", "snap camera",
        "xsplit vcam", "manycam", "newtek ndi"
    ];

    // ──────────────────────────────────────────────
    // Device Enumeration
    // ──────────────────────────────────────────────

    /// <summary>
    /// Enumerates available camera devices using DirectShow COM enumeration.
    /// DirectShow returns devices in the same order as OpenCV indices — no
    /// need to probe-open each device (which takes 1-3 seconds per index).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public List<CameraDeviceInfo> GetAvailableDevices()
    {
        var dsNames = GetDirectShowVideoDeviceNames();
        var devices = new List<CameraDeviceInfo>();

        for (int i = 0; i < dsNames.Count; i++)
        {
            devices.Add(new CameraDeviceInfo
            {
                Index = i,
                Name = dsNames[i],
                IsPhoneCamera = IsPhoneCameraName(dsNames[i]),
            });
        }

        return devices;
    }

    /// <summary>
    /// Enumerates video capture devices via DirectShow COM interfaces.
    /// Returns friendly names in the exact order that matches OpenCV device indices.
    /// This is the only reliable way to correlate device names with OpenCV indices on Windows.
    /// </summary>
    // DirectShow CLSIDs (Windows SDK constants)
    private static readonly Guid CLSID_SystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    private static readonly Guid CLSID_VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<string> GetDirectShowVideoDeviceNames()
    {
        var names = new List<string>();

        try
        {
            var devEnumType = Type.GetTypeFromCLSID(CLSID_SystemDeviceEnum);
            if (devEnumType == null) return names;

            var devEnum = (ICreateDevEnum)Activator.CreateInstance(devEnumType)!;
            var category = CLSID_VideoInputDeviceCategory;

            if (devEnum.CreateClassEnumerator(ref category, out var enumMoniker, 0) != 0
                || enumMoniker == null)
            {
                Marshal.ReleaseComObject(devEnum);
                return names;
            }

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                try
                {
                    var iid = typeof(IPropertyBag_).GUID;
                    monikers[0].BindToStorage(null!, null!, ref iid, out var bagObj);
                    try
                    {
                        if (bagObj is IPropertyBag_ bag)
                        {
                            bag.Read("FriendlyName", out var nameVar, IntPtr.Zero);
                            names.Add(nameVar?.ToString() ?? "Unknown");
                        }
                    }
                    finally
                    {
                        if (bagObj != null)
                            Marshal.ReleaseComObject(bagObj);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(monikers[0]);
                }
            }

            Marshal.ReleaseComObject(enumMoniker);
            Marshal.ReleaseComObject(devEnum);
        }
        catch
        {
            // DirectShow not available — fall back to WMI
            return GetWmiCameraDeviceNames();
        }

        return names;
    }

    /// <summary>
    /// Fallback: WMI device name enumeration (order may not match OpenCV indices).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<string> GetWmiCameraDeviceNames()
    {
        var names = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity " +
                "WHERE PNPClass = 'Image' OR PNPClass = 'Camera' " +
                "OR (PNPClass = 'SoftwareDevice' AND Name LIKE '%camera%')");

            foreach (var obj in searcher.Get())
                names.Add(obj["Name"]?.ToString() ?? "Unknown");
        }
        catch { }
        return names;
    }

    // ─── DirectShow COM Interop (minimal) ───

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(
            ref Guid clsidDeviceClass,
            out IEnumMoniker ppEnumMoniker,
            int dwFlags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag_
    {
        [PreserveSig]
        int Read(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
            [MarshalAs(UnmanagedType.Struct)] out object? pVar,
            IntPtr pErrorLog);

        [PreserveSig]
        int Write(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
            [MarshalAs(UnmanagedType.Struct)] ref object pVar);
    }

    /// <summary>
    /// Checks if a device name matches known phone/virtual camera patterns.
    /// </summary>
    private static bool IsPhoneCameraName(string name)
    {
        var lower = name.ToLowerInvariant();
        foreach (var pattern in PhoneCameraPatterns)
        {
            if (lower.Contains(pattern))
                return true;
        }
        return false;
    }

    // ──────────────────────────────────────────────
    // Auto-Select
    // ──────────────────────────────────────────────

    /// <summary>
    /// Selects the best camera device based on configuration preferences.
    ///
    /// Priority:
    ///   1. PreferredDeviceName match (case-insensitive contains)
    ///   2. Phone camera (if PreferPhoneCamera is true)
    ///   3. First physical (non-phone) camera
    ///   4. First device in list
    /// </summary>
    public CameraDeviceInfo? AutoSelectDevice(List<CameraDeviceInfo> devices)
    {
        if (devices.Count == 0)
            return null;

        // 1. Preferred device name match
        if (!string.IsNullOrWhiteSpace(PreferredDeviceName))
        {
            var preferred = devices.FirstOrDefault(d =>
                d.Name.Contains(PreferredDeviceName, StringComparison.OrdinalIgnoreCase));
            if (preferred != null)
                return preferred;
        }

        // 2. Phone camera preference
        if (PreferPhoneCamera)
        {
            var phone = devices.FirstOrDefault(d => d.IsPhoneCamera);
            if (phone != null)
                return phone;
        }

        // 3. First physical camera
        var physical = devices.FirstOrDefault(d => !d.IsPhoneCamera);
        if (physical != null)
            return physical;

        // 4. Fallback: first device
        return devices[0];
    }

    // ──────────────────────────────────────────────
    // Pre-warm / Start / Stop
    // ──────────────────────────────────────────────

    private volatile bool _isPreWarmed;
    private int _preWarmIndex = -1;

    /// <summary>
    /// Pre-opens the camera hardware in the background so that Start() is near-instant.
    /// Call from a background thread when the user selects a device (before they click Start).
    /// </summary>
    public bool PreWarm(CameraDeviceInfo device)
    {
        if (_isRunning)
            return true;

        lock (_lock)
        {
            // Re-check under lock — Start() may have set _isRunning between the
            // volatile read above and acquiring the lock
            if (_isRunning)
                return true;

            // Already pre-warmed for this device
            if (_isPreWarmed && _preWarmIndex == device.Index && _capture != null && _capture.IsOpened())
                return true;

            // Release any previous pre-warm
            if (_capture != null)
            {
                _capture.Release();
                _capture.Dispose();
                _capture = null;
                _isPreWarmed = false;
            }

            try
            {
                _capture = OpenCamera(device.Index);
                if (_capture == null)
                    return false;

                // Read and discard warm-up frames (eliminates ~400ms first-frame spike)
                using var warmup = new Mat();
                for (int i = 0; i < 3; i++)
                    _capture.Read(warmup);

                _isPreWarmed = true;
                _preWarmIndex = device.Index;
                return true;
            }
            catch
            {
                _capture?.Dispose();
                _capture = null;
                return false;
            }
        }
    }

    /// <summary>
    /// Opens a camera with DSHOW backend and configures MJPG + resolution.
    /// DSHOW is 2-4x faster than MSMF for init on Windows.
    /// MJPG is set before resolution to avoid device reinitialization.
    /// </summary>
    private static VideoCapture? OpenCamera(int cameraIndex)
    {
        // Always use DSHOW — it's 2-4x faster than MSMF for init
        var capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
        if (!capture.IsOpened())
        {
            capture.Dispose();
            return null;
        }

        // Set FOURCC first (MJPG = compressed USB transfer, avoids device reinit on resolution change)
        capture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));

        // Then resolution
        capture.Set(VideoCaptureProperties.FrameWidth, RecognitionSettings.CameraWidth);
        capture.Set(VideoCaptureProperties.FrameHeight, RecognitionSettings.CameraHeight);

        // Buffer size last (reduces latency — not all drivers honor this)
        capture.Set(VideoCaptureProperties.BufferSize, 1);

        return capture;
    }

    /// <summary>
    /// Start the capture loop. If PreWarm() was called, this is near-instant.
    /// </summary>
    public bool Start(int cameraIndex = 0)
    {
        if (_isRunning)
            return true;

        lock (_lock)
        {
            try
            {
                // Use pre-warmed capture if available for this index
                if (!(_isPreWarmed && _preWarmIndex == cameraIndex && _capture != null && _capture.IsOpened()))
                {
                    // No pre-warm — open fresh
                    _capture?.Dispose();
                    _capture = OpenCamera(cameraIndex);
                    if (_capture == null)
                    {
                        CameraError?.Invoke(this,
                            $"Failed to open camera at index {cameraIndex}. " +
                            "Make sure a webcam is connected and not in use by another application.");
                        return false;
                    }
                }

                _isPreWarmed = false;
                _isRunning = true;
                _fpsTimer = DateTime.UtcNow;
                _frameCount = 0;
                TotalFrames = 0;

                _captureThread = new Thread(CaptureLoop)
                {
                    IsBackground = true,
                    Name = "CameraCapture",
                    Priority = ThreadPriority.AboveNormal
                };
                _captureThread.Start();

                return true;
            }
            catch (Exception ex)
            {
                CameraError?.Invoke(this, $"Camera initialization error: {ex.Message}");
                _capture?.Dispose();
                _capture = null;
                _isRunning = false;
                return false;
            }
        }
    }

    /// <summary>
    /// Start capture for a specific device.
    /// </summary>
    public bool Start(CameraDeviceInfo device)
    {
        return Start(device.Index);
    }

    /// <summary>
    /// Stop the capture loop and release the webcam.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _captureThread?.Join(TimeSpan.FromSeconds(2));

        lock (_lock)
        {
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
            _isPreWarmed = false;
        }
    }

    // ──────────────────────────────────────────────
    // Capture Loop (runs on background thread)
    // ──────────────────────────────────────────────

    private void CaptureLoop()
    {
        // Reuse Mat across frames to avoid GC pressure
        using var frame = new Mat();

        while (_isRunning)
        {
            try
            {
                bool success;
                lock (_lock)
                {
                    if (_capture == null || !_capture.IsOpened())
                        break;

                    success = _capture.Read(frame);
                }

                if (!success || frame.Empty())
                {
                    // Brief pause before retry (camera may need time)
                    Thread.Sleep(10);
                    continue;
                }

                // Update FPS counter
                TotalFrames++;
                _frameCount++;
                var elapsed = (DateTime.UtcNow - _fpsTimer).TotalSeconds;
                if (elapsed >= 1.0)
                {
                    CurrentFps = _frameCount / elapsed;
                    _frameCount = 0;
                    _fpsTimer = DateTime.UtcNow;
                }

                // Fire event (clone the Mat so subscribers can process it safely)
                // We clone because the Mat is reused in the next iteration
                FrameCaptured?.Invoke(this, new FrameEventArgs(frame.Clone(), TotalFrames));
            }
            catch (Exception ex)
            {
                CameraError?.Invoke(this, $"Capture error: {ex.Message}");
                Thread.Sleep(100); // Avoid tight error loop
            }
        }
    }

    // ──────────────────────────────────────────────
    // Snapshot
    // ──────────────────────────────────────────────

    /// <summary>
    /// Capture a single frame (useful for registration).
    /// Returns null if camera isn't running.
    /// </summary>
    public Mat? CaptureSnapshot()
    {
        if (!_isRunning || _capture == null)
            return null;

        lock (_lock)
        {
            var frame = new Mat();
            if (_capture.Read(frame) && !frame.Empty())
                return frame;

            frame.Dispose();
            return null;
        }
    }

    // ──────────────────────────────────────────────
    // Cleanup
    // ──────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Event args for camera frame capture.
/// </summary>
public class FrameEventArgs : EventArgs
{
    /// <summary>
    /// The captured frame. Caller is responsible for disposing.
    /// </summary>
    public Mat Frame { get; }

    /// <summary>
    /// Frame sequence number (incrementing from 1).
    /// Used for frame skipping (process every Nth frame).
    /// </summary>
    public long FrameNumber { get; }

    /// <summary>
    /// Should this frame be processed by the AI pipeline?
    /// Based on frame skipping setting (every 6th frame by default).
    /// </summary>
    public bool ShouldProcess =>
        FrameNumber % RecognitionSettings.ProcessEveryNFrames == 0;

    public FrameEventArgs(Mat frame, long frameNumber)
    {
        Frame = frame;
        FrameNumber = frameNumber;
    }
}
