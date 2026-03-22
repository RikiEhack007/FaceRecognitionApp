using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FaceRecApp.Core.Entities;

/// <summary>
/// Audit log for every face recognition attempt.
/// </summary>
[Table("RecognitionLogs")]
public class RecognitionLog
{
    [Key]
    public int Id { get; set; }

    // ─── Who was matched? ───

    /// <summary>
    /// PID of the matched patient. NULL if face was not recognized.
    /// </summary>
    [Column("PID", TypeName = "varchar(10)")]
    public string? PID { get; set; }

    [ForeignKey(nameof(PID))]
    public Patient? Patient { get; set; }

    // ─── Match Quality ───

    /// <summary>
    /// Cosine distance from VECTOR_DISTANCE().
    /// 0.0 = perfect match, 1.0 = no match.
    /// </summary>
    public float Distance { get; set; }

    /// <summary>
    /// Cosine similarity (1 - Distance). Computed, not stored.
    /// </summary>
    [NotMapped]
    public float Similarity => 1f - Distance;

    public bool WasRecognized { get; set; }

    // ─── Liveness ───

    public bool PassedLiveness { get; set; }

    // ─── Context ───

    [Column(TypeName = "varchar(50)")]
    public string? StationId { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
