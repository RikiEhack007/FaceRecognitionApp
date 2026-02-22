namespace FaceRecApp.Core.Entities;

/// <summary>
/// Configuration constants for the face recognition system.
/// 
/// IMPORTANT — Understanding thresholds:
/// 
/// SQL Server's VECTOR_DISTANCE('cosine', a, b) returns DISTANCE (not similarity):
///   - 0.0 = identical vectors (perfect match)
///   - 1.0 = completely different (no match)
///   - 2.0 = opposite vectors
/// 
/// Traditional face recognition uses SIMILARITY = 1 - distance:
///   - 1.0 = identical
///   - 0.0 = completely different
/// 
/// FaceAiSharp recommends similarity threshold ≥ 0.42 for recognition.
/// That translates to distance ≤ 0.58.
/// 
/// For the PoC, we use 0.55 distance (0.45 similarity) — slightly stricter.
/// Tune this based on your testing:
///   - Lower distance threshold = fewer false accepts, more false rejects
///   - Higher distance threshold = more false accepts, fewer false rejects
/// </summary>
public static class RecognitionSettings
{
    // ─── Distance Thresholds ───
    // Remember: LOWER distance = BETTER match

    /// <summary>
    /// Maximum distance to consider a match.
    /// Distance ≤ this → person is recognized.
    /// Default: 0.55 (similarity ≥ 0.45)
    /// </summary>
    public const float DistanceThreshold = 0.55f;

    /// <summary>
    /// Distance below this = high confidence match.
    /// Used for UI display (green vs yellow indicator).
    /// Default: 0.35 (similarity ≥ 0.65)
    /// </summary>
    public const float HighConfidenceDistance = 0.35f;

    // ─── Embedding Dimensions ───
    /// <summary>
    /// ArcFace model output dimensions (fixed by the model).
    /// Do NOT change this unless you switch to a different model.
    /// </summary>
    public const int EmbeddingDimensions = 512;

    // ─── Enrollment ───
    /// <summary>
    /// Minimum face samples required during registration.
    /// More samples = better accuracy. 3 is a good balance.
    /// </summary>
    public const int MinEnrollmentSamples = 1;

    /// <summary>
    /// Recommended number of face samples for best accuracy.
    /// Ideally from different angles: front, slight-left, slight-right.
    /// </summary>
    public const int RecommendedEnrollmentSamples = 3;

    // ─── Camera ───
    /// <summary>
    /// Process face recognition every Nth frame.
    /// Camera runs at ~30fps; AI processing at ~5fps is sufficient.
    /// This keeps the UI smooth while running recognition in background.
    /// </summary>
    public const int ProcessEveryNFrames = 6;

    /// <summary>
    /// Default webcam resolution (width).
    /// 640×480 is fast and sufficient for face detection.
    /// Increase to 1280×720 if faces are far from camera.
    /// </summary>
    public const int CameraWidth = 640;
    public const int CameraHeight = 480;

    // ─── Liveness ───
    /// <summary>
    /// Number of recent eye-state frames to track for blink detection.
    /// At 5fps AI processing, 15 frames ≈ 3 seconds of history.
    /// </summary>
    public const int BlinkHistorySize = 15;

    /// <summary>
    /// Minimum number of blinks required for liveness confirmation.
    /// 2 blinks prevents single-fluke false positives.
    /// </summary>
    public const int MinBlinksRequired = 2;

    /// <summary>
    /// Seconds after liveness confirmation before requiring re-verification.
    /// Prevents "blink once, live forever" exploit.
    /// </summary>
    public const double LivenessExpirySeconds = 30.0;

    /// <summary>
    /// Cosine distance threshold for identity change detection.
    /// If the current embedding is this far from the last confirmed embedding,
    /// liveness resets (different person appeared).
    /// </summary>
    public const float IdentityChangeDistance = 0.40f;

    /// <summary>
    /// Number of face center positions to track for micro-movement analysis.
    /// At 5fps, 20 frames ≈ 4 seconds of movement history.
    /// </summary>
    public const int MicroMovementHistorySize = 20;

    /// <summary>
    /// Minimum standard deviation of face center positions (in pixels).
    /// Real faces exhibit natural sway of ~2-8px; photos are &lt; 0.5px.
    /// </summary>
    public const float MinMicroMovementStdDev = 1.5f;

    /// <summary>
    /// Minimum number of frames eyes must be closed for a valid blink.
    /// At 5fps, 1 frame = ~200ms (natural blink minimum).
    /// </summary>
    public const int MinBlinkDurationFrames = 1;

    /// <summary>
    /// Maximum number of frames eyes can be closed and still count as a blink.
    /// At 5fps, 3 frames = ~600ms. Longer = deliberate close, not a blink.
    /// </summary>
    public const int MaxBlinkDurationFrames = 3;

    /// <summary>
    /// Minimum Laplacian variance for face texture.
    /// Real skin has rich texture detail; screens/printouts have lower variance
    /// due to pixel resampling and printing artifacts.
    /// </summary>
    public const double MinLaplacianVariance = 120.0;

    /// <summary>
    /// Minimum color channel standard deviation across the face region.
    /// Real skin has varied color distribution; phone screens have uniform backlit colors.
    /// </summary>
    public const double MinColorVariation = 18.0;

    /// <summary>
    /// Maximum ratio of max brightness to mean brightness in face region.
    /// Phone screens reflecting ambient light create specular hotspots with high ratios.
    /// Real faces under normal lighting: ~1.5-2.5. Screens with reflection: ~3.0+.
    /// </summary>
    public const double MaxSpecularRatio = 3.0;

    // ─── ML Anti-Spoofing ───

    /// <summary>
    /// Minimum confidence for "Real" classification from MiniFASNetV2 model.
    /// Faces below this threshold are flagged as spoof.
    /// Default: 0.5 (model softmax output ranges 0-1).
    /// </summary>
    public const float AntiSpoofThreshold = 0.5f;

    /// <summary>
    /// Enable/disable texture analysis check.
    /// Can be disabled if causing false positives in certain lighting conditions.
    /// </summary>
    public const bool EnableTextureAnalysis = true;

    /// <summary>
    /// Number of consecutive texture analysis failures before flagging as spoof.
    /// Prevents single-frame false positives from shadows or lighting changes.
    /// </summary>
    public const int TextureFailFramesRequired = 5;

    /// <summary>
    /// Number of consecutive no-face frames before auto-resetting liveness.
    /// Prevents maintaining liveness status after walking away.
    /// </summary>
    public const int NoFaceResetFrames = 3;
}
