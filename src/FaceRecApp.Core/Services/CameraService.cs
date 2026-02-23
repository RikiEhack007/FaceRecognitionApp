using System.Management;
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
    /// Enumerates available camera devices by combining WMI device names
    /// with OpenCV probe results. Tags phone/virtual cameras via pattern matching.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public List<CameraDeviceInfo> GetAvailableDevices()
    {
        var wmiDevices = GetWmiCameraDevices();
        var devices = new List<CameraDeviceInfo>();

        for (int i = 0; i < MaxProbeDevices; i++)
        {
            try
            {
                using var testCapture = new VideoCapture(i);
                if (!testCapture.IsOpened())
                    break; // No more devices

                // Correlate with WMI by index (heuristic — standard approach)
                string name = i < wmiDevices.Count ? wmiDevices[i].name : $"Camera {i}";
                string deviceId = i < wmiDevices.Count ? wmiDevices[i].deviceId : "";
                bool isPhone = IsPhoneCameraName(name);

                devices.Add(new CameraDeviceInfo
                {
                    Index = i,
                    Name = name,
                    IsPhoneCamera = isPhone,
                    DeviceId = deviceId
                });
            }
            catch
            {
                // Device probe failed — skip this index
            }
        }

        return devices;
    }

    /// <summary>
    /// Queries WMI for camera/imaging devices with friendly names.
    /// Searches PNPClass Image/Camera and also SoftwareDevice entries
    /// that contain "camera" in their name (catches Phone Link, virtual cameras).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<(string name, string deviceId)> GetWmiCameraDevices()
    {
        var results = new List<(string name, string deviceId)>();

        try
        {
            // Query 1: Traditional hardware cameras (PNPClass = Image or Camera)
            // Query 2: Virtual/software cameras like Phone Link (PNPClass = SoftwareDevice, name contains camera)
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, PNPClass FROM Win32_PnPEntity " +
                "WHERE PNPClass = 'Image' OR PNPClass = 'Camera' " +
                "OR (PNPClass = 'SoftwareDevice' AND Name LIKE '%camera%')");

            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "Unknown";
                var deviceId = obj["DeviceID"]?.ToString() ?? "";
                results.Add((name, deviceId));
            }
        }
        catch
        {
            // WMI not available — fallback to index-based names
        }

        return results;
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
    // Start / Stop
    // ──────────────────────────────────────────────

    /// <summary>
    /// Open the webcam and start the capture loop.
    /// </summary>
    /// <param name="cameraIndex">Camera device index (0 = default webcam)</param>
    /// <param name="useDirectShow">Use DirectShow backend (better for virtual cameras)</param>
    /// <returns>true if camera opened successfully</returns>
    public bool Start(int cameraIndex = 0, bool useDirectShow = false)
    {
        if (_isRunning)
            return true;

        lock (_lock)
        {
            try
            {
                _capture = useDirectShow
                    ? new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW)
                    : new VideoCapture(cameraIndex);

                if (!_capture.IsOpened())
                {
                    CameraError?.Invoke(this,
                        $"Failed to open camera at index {cameraIndex}. " +
                        "Make sure a webcam is connected and not in use by another application.");
                    _capture.Dispose();
                    _capture = null;
                    return false;
                }

                // Set resolution
                _capture.Set(VideoCaptureProperties.FrameWidth, RecognitionSettings.CameraWidth);
                _capture.Set(VideoCaptureProperties.FrameHeight, RecognitionSettings.CameraHeight);

                // Try to set buffer size to 1 (reduces latency)
                _capture.Set(VideoCaptureProperties.BufferSize, 1);

                _isRunning = true;
                _fpsTimer = DateTime.UtcNow;
                _frameCount = 0;
                TotalFrames = 0;

                // Start capture on a dedicated background thread
                _captureThread = new Thread(CaptureLoop)
                {
                    IsBackground = true,  // Won't prevent app exit
                    Name = "CameraCapture",
                    Priority = ThreadPriority.AboveNormal  // Smooth video
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
    /// Open a specific camera device and start the capture loop.
    /// Uses DirectShow backend automatically for phone/virtual cameras.
    /// </summary>
    public bool Start(CameraDeviceInfo device)
    {
        return Start(device.Index, useDirectShow: device.IsPhoneCamera);
    }

    /// <summary>
    /// Stop the capture loop and release the webcam.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;

        // Wait for capture thread to finish (with timeout)
        _captureThread?.Join(TimeSpan.FromSeconds(2));

        lock (_lock)
        {
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
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
