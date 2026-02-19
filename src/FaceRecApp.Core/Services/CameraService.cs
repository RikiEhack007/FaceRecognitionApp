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

    // ──────────────────────────────────────────────
    // Start / Stop
    // ──────────────────────────────────────────────

    /// <summary>
    /// Open the webcam and start the capture loop.
    /// </summary>
    /// <param name="cameraIndex">Camera device index (0 = default webcam)</param>
    /// <returns>true if camera opened successfully</returns>
    public bool Start(int cameraIndex = 0)
    {
        if (_isRunning)
            return true;

        lock (_lock)
        {
            try
            {
                _capture = new VideoCapture(cameraIndex);

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
