using FaceRecApp.Core.Entities;
using SixLabors.ImageSharp;

namespace FaceRecApp.Core.Services;

/// <summary>
/// Result of processing a single detected face through the recognition pipeline.
/// 
/// One frame can contain multiple faces → multiple RecognitionResults.
/// 
/// Flow: Camera frame → FaceDetector → FaceRecognizer → SQL Vector Search → this result.
/// </summary>
public class RecognitionResult
{
    // ─── Who is this? ───

    /// <summary>
    /// Matched person from the database. NULL if not recognized.
    /// </summary>
    public Person? Person { get; set; }

    /// <summary>
    /// Was this face matched to someone in the database?
    /// True if Distance ≤ threshold.
    /// </summary>
    public bool IsRecognized { get; set; }

    // ─── Match Quality ───

    /// <summary>
    /// Cosine distance from VECTOR_DISTANCE().
    /// 0.0 = perfect match, 1.0 = no match.
    /// </summary>
    public float Distance { get; set; } = 1.0f;

    /// <summary>
    /// Cosine similarity (1 - Distance) as percentage string.
    /// Example: "87.3%"
    /// </summary>
    public string SimilarityText => $"{(1f - Distance) * 100:F1}%";

    /// <summary>
    /// Is the match high confidence (distance below the stricter threshold)?
    /// Used for UI: green box vs yellow box.
    /// </summary>
    public bool IsHighConfidence =>
        IsRecognized && Distance <= RecognitionSettings.HighConfidenceDistance;

    // ─── Liveness & Anti-Spoofing ───

    /// <summary>
    /// Did this face pass the liveness check (blink detection)?
    /// False means possible spoofing attempt.
    /// </summary>
    public bool IsLive { get; set; } = false;

    /// <summary>
    /// Did per-face texture analysis detect this as a likely spoof?
    /// True = face is on a phone screen, printout, or other reproduction.
    /// This is independent of liveness — a face can be "not yet live" (pending blinks)
    /// but not spoofed (real face that hasn't blinked yet).
    /// </summary>
    public bool IsSpoofDetected { get; set; }

    // ─── Face Location ───

    /// <summary>
    /// Bounding box of the face in the original frame.
    /// Used for drawing overlays.
    /// </summary>
    public RectangleF FaceBox { get; set; }

    // ─── Embedding ───

    /// <summary>
    /// The 512-dimensional embedding generated for this face.
    /// Stored here temporarily — used for registration if the user decides
    /// to register this face.
    /// </summary>
    public float[]? Embedding { get; set; }

    // ─── Timing ───

    /// <summary>
    /// Time spent on face detection for this frame.
    /// </summary>
    public TimeSpan DetectionTime { get; set; }

    /// <summary>
    /// Time spent generating the embedding.
    /// </summary>
    public TimeSpan EmbeddingTime { get; set; }

    /// <summary>
    /// Time spent on SQL vector search.
    /// </summary>
    public TimeSpan SearchTime { get; set; }

    /// <summary>
    /// Total processing time for this face.
    /// </summary>
    public TimeSpan TotalTime => DetectionTime + EmbeddingTime + SearchTime;

    // ─── Display Helpers ───

    /// <summary>
    /// Label for the face overlay box.
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            if (IsSpoofDetected)
                return "SPOOF DETECTED";

            string name;
            if (IsRecognized && Person != null)
                name = $"{Person.Name} ({SimilarityText})";
            else
                name = $"Unknown ({SimilarityText})";

            if (!IsLive)
                return name + " [!]";
            return name;
        }
    }
}
