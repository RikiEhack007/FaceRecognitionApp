using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FaceRecApp.Core.Entities;

/// <summary>
/// Audit log for every face recognition attempt.
/// 
/// Captures both successful and failed attempts for:
///   - Security auditing (who accessed when?)
///   - Performance analytics (what's the average confidence?)
///   - Spoofing tracking (how many liveness failures?)
///   - Threshold tuning (analyze false accepts/rejects)
/// </summary>
public class RecognitionLog
{
    [Key]
    public int Id { get; set; }

    // ─── Who was matched? ───
    /// <summary>
    /// PersonId of the matched person. NULL if face was not recognized.
    /// </summary>
    public int? PersonId { get; set; }

    [ForeignKey(nameof(PersonId))]
    public Person? Person { get; set; }

    // ─── Match Quality ───
    /// <summary>
    /// Cosine distance from VECTOR_DISTANCE().
    /// 0.0 = perfect match, 1.0 = no match.
    /// Lower is better.
    /// </summary>
    public float Distance { get; set; }

    /// <summary>
    /// Cosine similarity (1 - Distance).
    /// 1.0 = perfect match, 0.0 = no match.
    /// Higher is better. This is more intuitive for display.
    /// </summary>
    [NotMapped]  // Computed, not stored in DB
    public float Similarity => 1f - Distance;

    /// <summary>
    /// Was the face successfully recognized (distance below threshold)?
    /// </summary>
    public bool WasRecognized { get; set; }

    // ─── Liveness ───
    /// <summary>
    /// Did the face pass the liveness/anti-spoofing check?
    /// </summary>
    public bool PassedLiveness { get; set; }

    // ─── Context ───
    /// <summary>
    /// Which station/kiosk performed the scan (for multi-terminal setups).
    /// </summary>
    [MaxLength(50)]
    public string? StationId { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
