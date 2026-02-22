using FaceAiSharp;
using FaceRecApp.Core.Entities;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FaceRecApp.Core.Services;

/// <summary>
/// Multi-layered liveness detection with state machine and anti-spoofing.
///
/// State machine: Pending (not live) → Confirmed (live) → Pending (on expiry/identity change).
///
/// Four anti-spoofing layers:
///   1. Blink detection — require 2+ blinks with natural duration (1-3 frames closed)
///   2. Blink periodicity — detect suspiciously regular blink intervals (video loops)
///   3. Micro-movement — track face center stddev; real faces sway, photos don't
///   4. Texture analysis — Laplacian variance on face region; screens/printouts have lower texture
///
/// What it catches:
///   ✅ Printed photo (no blinks + no movement + low texture)
///   ✅ Static digital photo on phone/screen (no blinks + no movement)
///   ✅ Video replay on phone (periodic blinks + low texture)
///   ✅ Person swap (embedding distance auto-reset)
///   ✅ Walk away (no-face auto-reset)
///   ❌ 3D masks — cannot detect
///   ❌ Deepfakes — cannot detect
///
/// Production would need: 3D/IR camera + iBeta-certified PAD module.
///
/// Thread safety: NOT thread-safe. Use from a single processing thread.
/// </summary>
public class LivenessService : IDisposable
{
    private readonly IEyeStateDetector _eyeDetector;
    private readonly Queue<EyeState> _eyeHistory;
    private bool _disposed;

    // ─── State Machine ───
    private LivenessState _state = LivenessState.Pending;
    private DateTime _confirmedAt;

    // ─── Identity Tracking ───
    private float[]? _lastConfirmedEmbedding;
    private float[]? _trackingEmbedding; // Tracks identity even before confirmation

    // ─── Blink Tracking ───
    private readonly List<DateTime> _blinkTimestamps = new();
    private int _closedFrameCount;
    private bool _wasClosedPreviously;

    // ─── Micro-Movement Tracking ───
    private readonly Queue<PointF> _facePositionHistory = new();

    // ─── Texture Analysis ───
    private int _consecutiveTextureFailures;
    private double _lastLaplacianVariance;
    private double _lastColorVariation;

    // ─── No-Face Tracking ───
    private int _noFaceFrameCount;

    /// <summary>
    /// Number of validated blinks detected since the last reset.
    /// </summary>
    public int BlinkCount { get; private set; }

    /// <summary>
    /// Current state of the tracked eyes.
    /// </summary>
    public EyeState CurrentEyeState { get; private set; } = EyeState.Unknown;

    /// <summary>
    /// Is the person currently confirmed as live?
    /// </summary>
    public bool IsLive => _state == LivenessState.Confirmed && !IsExpired();

    public LivenessService()
    {
        _eyeDetector = FaceAiSharpBundleFactory.CreateEyeStateDetector();
        _eyeHistory = new Queue<EyeState>(RecognitionSettings.BlinkHistorySize + 1);
    }

    // ══════════════════════════════════════════════
    //  Core Processing
    // ══════════════════════════════════════════════

    /// <summary>
    /// Process a detected face and update all anti-spoofing checks.
    /// Call this for every processed frame where a face is detected.
    /// </summary>
    /// <param name="image">Full image containing the face (for eye detection)</param>
    /// <param name="face">Detection result with landmarks</param>
    /// <param name="embedding">Optional 512-dim embedding for identity tracking</param>
    /// <param name="frame">Optional original BGR Mat for texture analysis (avoids extra conversion)</param>
    /// <returns>true if liveness is currently confirmed</returns>
    public bool ProcessFrame(Image<Rgb24> image, FaceDetectorResult face, float[]? embedding = null, Mat? frame = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Reset no-face counter since we have a face
        _noFaceFrameCount = 0;

        // Check for identity change (different person appeared)
        if (embedding != null)
            CheckIdentityChange(embedding);

        // Check for liveness expiry
        if (_state == LivenessState.Confirmed && IsExpired())
        {
            Console.WriteLine("[Liveness] Confirmation expired — re-verifying");
            _state = LivenessState.Pending;
            BlinkCount = 0;
            _blinkTimestamps.Clear();
            _eyeHistory.Clear();
        }

        // Track micro-movement
        TrackMicroMovement(face);

        // Texture analysis (use original Mat if available to avoid expensive ImageSharp→Mat conversion)
        if (RecognitionSettings.EnableTextureAnalysis && frame != null)
            AnalyzeTexture(frame, face);

        // Eye state / blink detection
        if (face.Landmarks != null && face.Landmarks.Count >= 5)
        {
            try
            {
                var eyeBoxes = ImageCalculations.GetEyeBoxesFromCenterPoints(
                    face.Landmarks![0], face.Landmarks[1], distanceDivisor: 2.4f);

                // Clamp eye crop rectangles to image bounds.
                // GetEyeBoxesFromCenterPoints can return boxes that extend outside the image
                // (e.g., face near edge), causing ArgumentOutOfRangeException in Crop().
                var leftBox = ClampToImageBounds(eyeBoxes.Left, image.Width, image.Height);
                var rightBox = ClampToImageBounds(eyeBoxes.Right, image.Width, image.Height);

                // Skip if eye regions are too small after clamping
                if (leftBox.Width < 5 || leftBox.Height < 5 ||
                    rightBox.Width < 5 || rightBox.Height < 5)
                {
                    Console.WriteLine("[Liveness] Eye boxes too small after clamping — skipping blink detection");
                }
                else
                {
                    using var leftEyeImage = image.Clone(ctx => ctx.Crop(leftBox));
                    using var rightEyeImage = image.Clone(ctx => ctx.Crop(rightBox));

                    bool leftOpen = _eyeDetector.IsOpen(leftEyeImage);
                    bool rightOpen = _eyeDetector.IsOpen(rightEyeImage);

                    CurrentEyeState = MapEyeState(leftOpen, rightOpen);

                    _eyeHistory.Enqueue(CurrentEyeState);
                    while (_eyeHistory.Count > RecognitionSettings.BlinkHistorySize)
                        _eyeHistory.Dequeue();

                    TrackBlinkDuration(CurrentEyeState);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Liveness] Eye detection error: {ex.Message}");
            }
        }

        // Evaluate combined liveness
        if (_state == LivenessState.Pending)
            EvaluateLiveness(embedding);

        return IsLive;
    }

    /// <summary>
    /// Called by the pipeline when no face is detected in a frame.
    /// After NoFaceResetFrames consecutive no-face frames, resets liveness.
    /// </summary>
    public void OnNoFaceDetected()
    {
        _noFaceFrameCount++;

        if (_noFaceFrameCount >= RecognitionSettings.NoFaceResetFrames)
            Reset();
    }

    // ══════════════════════════════════════════════
    //  Identity Change Detection
    // ══════════════════════════════════════════════

    private void CheckIdentityChange(float[] currentEmbedding)
    {
        // After confirmation: check against confirmed identity
        if (_lastConfirmedEmbedding != null)
        {
            float distance = FaceRecognitionService.CosineDistance(_lastConfirmedEmbedding, currentEmbedding);
            if (distance > RecognitionSettings.IdentityChangeDistance)
            {
                Reset();
                _trackingEmbedding = (float[])currentEmbedding.Clone();
            }
            return;
        }

        // During pending phase: track identity to prevent person-swap exploits.
        // Without this, person A blinks once, person B takes their place and blinks once,
        // and the system counts 2 blinks total — confirming B as live.
        if (_trackingEmbedding == null)
        {
            _trackingEmbedding = (float[])currentEmbedding.Clone();
        }
        else
        {
            float distance = FaceRecognitionService.CosineDistance(_trackingEmbedding, currentEmbedding);
            if (distance > RecognitionSettings.IdentityChangeDistance)
            {
                // Different person during pending — reset blink counts etc.
                Reset();
                _trackingEmbedding = (float[])currentEmbedding.Clone();
            }
        }
    }

    // ══════════════════════════════════════════════
    //  Blink Duration Tracking
    // ══════════════════════════════════════════════

    /// <summary>
    /// Track how long eyes stay closed to validate natural blink timing.
    /// Natural blinks last 1-3 frames at 5fps (~200-600ms).
    /// </summary>
    private void TrackBlinkDuration(EyeState state)
    {
        if (state == EyeState.Closed)
        {
            _closedFrameCount++;
            _wasClosedPreviously = true;
        }
        else if (state == EyeState.Open && _wasClosedPreviously)
        {
            // Transition from closed → open: validate blink duration
            if (_closedFrameCount >= RecognitionSettings.MinBlinkDurationFrames &&
                _closedFrameCount <= RecognitionSettings.MaxBlinkDurationFrames)
            {
                // Valid natural blink
                BlinkCount++;
                _blinkTimestamps.Add(DateTime.UtcNow);
                Console.WriteLine($"[Liveness] BLINK #{BlinkCount} detected (closed for {_closedFrameCount} frames)");
            }
            else
            {
                Console.WriteLine($"[Liveness] Eyes closed→open but duration {_closedFrameCount} frames out of range [{RecognitionSettings.MinBlinkDurationFrames}-{RecognitionSettings.MaxBlinkDurationFrames}]");
            }

            _closedFrameCount = 0;
            _wasClosedPreviously = false;
        }
        else
        {
            // Eyes open and were open before — reset
            _closedFrameCount = 0;
            _wasClosedPreviously = false;
        }
    }

    /// <summary>
    /// Detect suspiciously regular blink intervals that suggest video replay.
    /// Natural blinks have irregular timing (CV > 0.15); video loops are periodic.
    /// </summary>
    private bool HasNaturalBlinkTiming()
    {
        if (_blinkTimestamps.Count < 3)
            return true; // Not enough data to judge — give benefit of the doubt

        var intervals = new List<double>();
        for (int i = 1; i < _blinkTimestamps.Count; i++)
            intervals.Add((_blinkTimestamps[i] - _blinkTimestamps[i - 1]).TotalSeconds);

        double mean = intervals.Average();
        if (mean < 0.001) return false; // Avoid division by zero

        double variance = intervals.Sum(x => (x - mean) * (x - mean)) / intervals.Count;
        double stdDev = Math.Sqrt(variance);
        double coefficientOfVariation = stdDev / mean;

        // Natural blinks: CV typically > 0.3. Video loops: CV ≈ 0.
        // Threshold at 0.15 to catch very regular patterns.
        return coefficientOfVariation > 0.15;
    }

    // ══════════════════════════════════════════════
    //  Micro-Movement Analysis
    // ══════════════════════════════════════════════

    /// <summary>
    /// Record the face center position for movement analysis.
    /// </summary>
    private void TrackMicroMovement(FaceDetectorResult face)
    {
        var box = face.Box;
        var center = new PointF(box.X + box.Width / 2f, box.Y + box.Height / 2f);

        _facePositionHistory.Enqueue(center);
        while (_facePositionHistory.Count > RecognitionSettings.MicroMovementHistorySize)
            _facePositionHistory.Dequeue();
    }

    /// <summary>
    /// Check if the face shows natural micro-movement.
    /// Real faces exhibit involuntary sway of ~2-8px; photos/screens are static (&lt; 0.5px).
    /// </summary>
    private bool HasNaturalMicroMovement()
    {
        if (_facePositionHistory.Count < RecognitionSettings.MicroMovementHistorySize / 2)
            return true; // Not enough data yet — don't block

        var positions = _facePositionHistory.ToArray();

        // Calculate stddev of X and Y positions
        float meanX = positions.Average(p => p.X);
        float meanY = positions.Average(p => p.Y);

        float varianceX = positions.Average(p => (p.X - meanX) * (p.X - meanX));
        float varianceY = positions.Average(p => (p.Y - meanY) * (p.Y - meanY));

        float stdDevX = MathF.Sqrt(varianceX);
        float stdDevY = MathF.Sqrt(varianceY);

        bool hasMovement = stdDevX >= RecognitionSettings.MinMicroMovementStdDev ||
                           stdDevY >= RecognitionSettings.MinMicroMovementStdDev;

        Console.WriteLine($"[Liveness] Movement: stdDevX={stdDevX:F2} stdDevY={stdDevY:F2} " +
                          $"(min={RecognitionSettings.MinMicroMovementStdDev}) " +
                          $"positions={positions.Length} natural={hasMovement}");

        return hasMovement;
    }

    // ══════════════════════════════════════════════
    //  Texture Analysis
    // ══════════════════════════════════════════════

    /// <summary>
    /// Analyze face texture using Laplacian variance on the original BGR Mat.
    /// Crops face ROI directly from the Mat — avoids the expensive ImageSharp→Mat
    /// JPEG encode/decode round-trip (~8-15ms saved per frame).
    ///
    /// Two checks:
    ///   1. Laplacian variance — real skin has high-frequency texture detail;
    ///      screens/printouts have lower variance due to pixel resampling.
    ///   2. Color channel uniformity — phone screens emit light uniformly;
    ///      real skin has uneven color distribution across the face.
    /// </summary>
    private void AnalyzeTexture(Mat frame, FaceDetectorResult face)
    {
        try
        {
            var box = face.Box;

            // Clamp box to frame bounds
            int x = Math.Max(0, (int)box.X);
            int y = Math.Max(0, (int)box.Y);
            int w = Math.Min((int)box.Width, frame.Width - x);
            int h = Math.Min((int)box.Height, frame.Height - y);

            if (w < 20 || h < 20) return; // Too small to analyze

            // Crop face region directly from Mat using ROI (no copy, sub-millisecond)
            using var faceRoi = new Mat(frame, new Rect(x, y, w, h));
            using var gray = new Mat();
            Cv2.CvtColor(faceRoi, gray, ColorConversionCodes.BGR2GRAY);

            // Check 1: Laplacian variance (texture sharpness)
            using var laplacian = new Mat();
            Cv2.Laplacian(gray, laplacian, MatType.CV_64F);
            Cv2.MeanStdDev(laplacian, out _, out var stddev);
            double laplacianVariance = stddev.Val0 * stddev.Val0;

            // Check 2: Color channel analysis for screen detection.
            // Phone screens have more uniform color distribution (backlit pixels)
            // vs. real skin which has varied color due to light absorption/reflection.
            Cv2.MeanStdDev(faceRoi, out var meanColor, out var colorStdDev);
            double colorVariation = (colorStdDev.Val0 + colorStdDev.Val1 + colorStdDev.Val2) / 3.0;

            // Check 3: Specular highlights / screen reflection detection.
            // Phone screens often have bright hotspots from ambient light reflection.
            double maxBrightness;
            Cv2.MinMaxLoc(gray, out _, out maxBrightness);
            Cv2.MeanStdDev(gray, out var grayMean, out _);
            double brightnessRatio = maxBrightness / Math.Max(grayMean.Val0, 1.0);

            _lastLaplacianVariance = laplacianVariance;
            _lastColorVariation = colorVariation;

            bool textureFail = laplacianVariance < RecognitionSettings.MinLaplacianVariance;
            bool colorFail = colorVariation < RecognitionSettings.MinColorVariation;
            // Specular highlight: if max brightness is 3x+ the mean, likely screen reflection
            bool specularFail = brightnessRatio > RecognitionSettings.MaxSpecularRatio;

            // Fail if texture is bad AND (color is uniform OR has specular highlights)
            // This reduces false positives — single checks can be noisy.
            bool isSuspicious = textureFail || (colorFail && specularFail);

            if (isSuspicious)
                _consecutiveTextureFailures++;
            else
                _consecutiveTextureFailures = 0;

            Console.WriteLine($"[Liveness] Texture: laplacian={laplacianVariance:F1} " +
                              $"(min={RecognitionSettings.MinLaplacianVariance}) " +
                              $"colorVar={colorVariation:F1} (min={RecognitionSettings.MinColorVariation}) " +
                              $"specular={brightnessRatio:F2} " +
                              $"fails={_consecutiveTextureFailures}/{RecognitionSettings.TextureFailFramesRequired}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Liveness] Texture analysis error: {ex.Message}");
        }
    }

    /// <summary>
    /// Has texture analysis detected a likely spoof?
    /// Only flags after multiple consecutive failures to avoid single-frame false positives.
    /// </summary>
    private bool HasNaturalTexture()
    {
#pragma warning disable CS0162 // Unreachable code (EnableTextureAnalysis is a compile-time toggle)
        if (!RecognitionSettings.EnableTextureAnalysis)
            return true;
#pragma warning restore CS0162

        return _consecutiveTextureFailures < RecognitionSettings.TextureFailFramesRequired;
    }

    // ══════════════════════════════════════════════
    //  Liveness Evaluation
    // ══════════════════════════════════════════════

    /// <summary>
    /// Combine all anti-spoofing checks to determine liveness.
    /// All four layers must pass for confirmation.
    /// </summary>
    private void EvaluateLiveness(float[]? embedding)
    {
        bool enoughBlinks = BlinkCount >= RecognitionSettings.MinBlinksRequired;
        bool naturalTiming = HasNaturalBlinkTiming();
        bool naturalMovement = HasNaturalMicroMovement();
        bool naturalTexture = HasNaturalTexture();

        Console.WriteLine($"[Liveness] Evaluate: blinks={BlinkCount}/{RecognitionSettings.MinBlinksRequired} " +
                          $"timing={naturalTiming} movement={naturalMovement} texture={naturalTexture} " +
                          $"textureFailures={_consecutiveTextureFailures} positions={_facePositionHistory.Count}");

        if (enoughBlinks && naturalTiming && naturalMovement && naturalTexture)
        {
            _state = LivenessState.Confirmed;
            _confirmedAt = DateTime.UtcNow;
            Console.WriteLine("[Liveness] *** CONFIRMED LIVE ***");

            if (embedding != null)
                _lastConfirmedEmbedding = (float[])embedding.Clone();
        }
    }

    // ══════════════════════════════════════════════
    //  State Management
    // ══════════════════════════════════════════════

    /// <summary>
    /// Check if liveness confirmation has expired.
    /// </summary>
    private bool IsExpired()
    {
        if (_state != LivenessState.Confirmed)
            return false;

        return (DateTime.UtcNow - _confirmedAt).TotalSeconds > RecognitionSettings.LivenessExpirySeconds;
    }

    /// <summary>
    /// Reset all liveness state. Call when:
    ///   - Starting a new recognition session
    ///   - A different person appears in frame
    ///   - The person walks away
    ///   - The registration process begins
    /// </summary>
    public void Reset()
    {
        _state = LivenessState.Pending;
        _eyeHistory.Clear();
        BlinkCount = 0;
        _blinkTimestamps.Clear();
        _closedFrameCount = 0;
        _wasClosedPreviously = false;
        _facePositionHistory.Clear();
        _consecutiveTextureFailures = 0;
        _noFaceFrameCount = 0;
        _lastConfirmedEmbedding = null;
        _trackingEmbedding = null;
        CurrentEyeState = EyeState.Unknown;
    }

    /// <summary>
    /// Get a human-readable status string for the UI.
    /// </summary>
    public string GetStatusText()
    {
        if (_state == LivenessState.Confirmed)
        {
            if (IsExpired())
                return "Liveness expired -- re-verifying...";

            double remaining = RecognitionSettings.LivenessExpirySeconds -
                               (DateTime.UtcNow - _confirmedAt).TotalSeconds;
            return $"LIVE (blinks: {BlinkCount}, expires: {remaining:F0}s)";
        }

        // Check for spoof indicators
        if (!HasNaturalTexture())
            return $"SPOOF SUSPECTED -- texture: {_lastLaplacianVariance:F0}, color: {_lastColorVariation:F0}";

        if (_facePositionHistory.Count >= RecognitionSettings.MicroMovementHistorySize / 2 &&
            !HasNaturalMicroMovement())
            return "No natural movement detected -- possible photo/screen";

        if (BlinkCount > 0 && !HasNaturalBlinkTiming())
            return "Suspicious blink pattern -- possible video replay";

        int blinksNeeded = RecognitionSettings.MinBlinksRequired - BlinkCount;
        if (blinksNeeded > 0)
        {
            return CurrentEyeState switch
            {
                EyeState.Open => $"Eyes open -- need {blinksNeeded} more blink(s) ({_eyeHistory.Count}/{RecognitionSettings.BlinkHistorySize})",
                EyeState.Closed => "Eyes closed...",
                _ => "Detecting eye state..."
            };
        }

        // Has enough blinks but waiting for other checks
        return "Verifying liveness...";
    }

    // ══════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════

    /// <summary>
    /// Map individual eye open/closed booleans to our simplified enum.
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

    /// <summary>
    /// Clamp a rectangle to fit within image bounds.
    /// FaceAiSharp's GetEyeBoxesFromCenterPoints can return boxes that extend
    /// outside the image when a face is near the edge, causing ArgumentOutOfRangeException.
    /// </summary>
    private static SixLabors.ImageSharp.Rectangle ClampToImageBounds(
        SixLabors.ImageSharp.Rectangle rect, int imageWidth, int imageHeight)
    {
        int x = Math.Max(0, rect.X);
        int y = Math.Max(0, rect.Y);
        int right = Math.Min(imageWidth, rect.X + rect.Width);
        int bottom = Math.Min(imageHeight, rect.Y + rect.Height);

        int width = Math.Max(0, right - x);
        int height = Math.Max(0, bottom - y);

        return new SixLabors.ImageSharp.Rectangle(x, y, width, height);
    }

    // ══════════════════════════════════════════════
    //  Cleanup
    // ══════════════════════════════════════════════

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

/// <summary>
/// Liveness state machine states.
/// </summary>
public enum LivenessState
{
    /// <summary>Not yet confirmed as live. Default state.</summary>
    Pending,

    /// <summary>Liveness confirmed — all anti-spoofing checks passed.</summary>
    Confirmed
}

