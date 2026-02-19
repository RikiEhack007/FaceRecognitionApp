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
}
