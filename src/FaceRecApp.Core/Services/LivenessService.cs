using FaceAiSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using FaceRecApp.Core.Entities;

namespace FaceRecApp.Core.Services;

/// <summary>
/// Basic liveness detection using blink (eye state) tracking.
/// 
/// How it works:
///   1. FaceAiSharp can detect whether eyes are open or closed
///   2. We maintain a rolling history of eye states over recent frames
///   3. If we detect an open→closed→open transition = natural blink
///   4. A natural blink within the observation window = person is live
/// 
/// What it catches:
///   ✅ Printed photo (no blinks)
///   ✅ Static digital photo on phone/screen (no blinks)
///   ⚠️ Video replay on phone — sometimes catches, sometimes doesn't
///   ❌ 3D masks — cannot detect
///   ❌ Deepfakes — cannot detect
/// 
/// For the PoC, this is sufficient to demonstrate the concept.
/// Production would need: 3D/IR camera + iBeta-certified PAD module.
/// 
/// Thread safety: NOT thread-safe. Use from a single processing thread.
/// </summary>
public class LivenessService : IDisposable
{
    private readonly IEyeStateDetector _eyeDetector;
    private readonly Queue<EyeState> _eyeHistory;
    private bool _disposed;

    /// <summary>
    /// Has a natural blink been detected in the current observation window?
    /// </summary>
    public bool BlinkDetected { get; private set; }

    /// <summary>
    /// Number of blinks detected since the last reset.
    /// </summary>
    public int BlinkCount { get; private set; }

    /// <summary>
    /// Current state of the tracked eyes.
    /// </summary>
    public EyeState CurrentEyeState { get; private set; } = EyeState.Unknown;

    public LivenessService()
    {
        _eyeDetector = FaceAiSharpBundleFactory.CreateEyeStateDetector();
        _eyeHistory = new Queue<EyeState>(RecognitionSettings.BlinkHistorySize + 1);
    }

    // ──────────────────────────────────────────────
    // Core Processing
    // ──────────────────────────────────────────────

    /// <summary>
    /// Process a detected face and update the eye state history.
    /// Call this for every frame where a face is detected.
    /// 
    /// Returns true if the person appears to be live (blink detected).
    /// </summary>
    /// <param name="image">Full image containing the face</param>
    /// <param name="face">Detection result with landmarks</param>
    /// <returns>true if liveness confirmed (blink observed)</returns>
    public bool ProcessFrame(Image<Rgb24> image, FaceDetectorResult face)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (face.Landmarks == null || face.Landmarks.Count < 5)
            return BlinkDetected; // Can't check without landmarks

        try
        {
            // Extract eye regions from 5-point landmarks (indices 0=left eye, 1=right eye)
            var eyeBoxes = ImageCalculations.GetEyeBoxesFromCenterPoints(
                face.Landmarks![0], face.Landmarks[1], distanceDivisor: 2.4f);

            // Crop eye images and check if each is open
            using var leftEyeImage = image.Clone(ctx => ctx.Crop(eyeBoxes.Left));
            using var rightEyeImage = image.Clone(ctx => ctx.Crop(eyeBoxes.Right));

            bool leftOpen = _eyeDetector.IsOpen(leftEyeImage);
            bool rightOpen = _eyeDetector.IsOpen(rightEyeImage);

            CurrentEyeState = MapEyeState(leftOpen, rightOpen);

            // Add to rolling history
            _eyeHistory.Enqueue(CurrentEyeState);
            while (_eyeHistory.Count > RecognitionSettings.BlinkHistorySize)
                _eyeHistory.Dequeue();

            // Check for blink pattern in history
            if (DetectBlinkPattern())
            {
                BlinkDetected = true;
                BlinkCount++;
            }
        }
        catch
        {
            // Eye state detection can fail on partially visible faces
            // Just skip this frame
        }

        return BlinkDetected;
    }

    /// <summary>
    /// Process a face from an OpenCvSharp Mat frame.
    /// </summary>
    public bool ProcessFrame(OpenCvSharp.Mat frame, FaceDetectorResult face)
    {
        using var image = Helpers.ImageConverter.MatToImageSharp(frame);
        return ProcessFrame(image, face);
    }

    // ──────────────────────────────────────────────
    // Blink Pattern Detection
    // ──────────────────────────────────────────────

    /// <summary>
    /// Look for an open→closed→open pattern in the eye history.
    /// A natural blink lasts about 100-400ms (3-12 frames at 30fps,
    /// or roughly 1-2 frames at our 5fps processing rate).
    /// </summary>
    private bool DetectBlinkPattern()
    {
        if (_eyeHistory.Count < 3)
            return false;

        var states = _eyeHistory.ToArray();

        // Look for pattern: Open ... Closed ... Open
        // Walking backwards through history to find the most recent blink
        for (int i = states.Length - 1; i >= 2; i--)
        {
            // Current frame: eyes open (after blink)
            if (states[i] != EyeState.Open)
                continue;

            // Look backwards for closed eyes
            for (int j = i - 1; j >= 1; j--)
            {
                if (states[j] == EyeState.Closed)
                {
                    // Look further back for open eyes (before blink)
                    for (int k = j - 1; k >= 0; k--)
                    {
                        if (states[k] == EyeState.Open)
                        {
                            // Found: Open [k] → Closed [j] → Open [i]
                            // Verify the blink wasn't too long (max 5 frames ≈ 1 second)
                            if (i - k <= 5)
                                return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    // ──────────────────────────────────────────────
    // State Management
    // ──────────────────────────────────────────────

    /// <summary>
    /// Reset the liveness state. Call when:
    ///   - Starting a new recognition session
    ///   - A different person appears in frame
    ///   - The registration process begins
    /// </summary>
    public void Reset()
    {
        _eyeHistory.Clear();
        BlinkDetected = false;
        BlinkCount = 0;
        CurrentEyeState = EyeState.Unknown;
    }

    /// <summary>
    /// Get a human-readable status string for the UI.
    /// </summary>
    public string GetStatusText()
    {
        if (BlinkDetected)
            return $"✅ Live (blinks: {BlinkCount})";

        return CurrentEyeState switch
        {
            EyeState.Open => $"👁️ Eyes open — waiting for blink... ({_eyeHistory.Count}/{RecognitionSettings.BlinkHistorySize})",
            EyeState.Closed => "👁️ Eyes closed...",
            _ => "⏳ Detecting eye state..."
        };
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Map individual eye open/closed booleans to our simplified enum.
    /// For blink detection, we check if BOTH eyes are in the same state.
    /// A natural blink closes both eyes simultaneously.
    /// </summary>
    private static EyeState MapEyeState(bool leftOpen, bool rightOpen)
    {
        if (leftOpen && rightOpen)
            return EyeState.Open;

        if (!leftOpen && !rightOpen)
            return EyeState.Closed;

        // One eye open, one closed — could be a wink or partial detection
        // Treat as open (don't count as blink)
        return EyeState.Open;
    }

    // ──────────────────────────────────────────────
    // Cleanup
    // ──────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_eyeDetector is IDisposable disposable)
            disposable.Dispose();

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Simplified eye state for blink tracking.
/// </summary>
public enum EyeState
{
    Unknown,
    Open,
    Closed
}
