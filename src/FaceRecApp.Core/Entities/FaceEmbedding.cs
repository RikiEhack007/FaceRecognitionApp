using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FaceRecApp.Core.Entities;

/// <summary>
/// Stores a single face embedding (512-dimensional vector from ArcFace model).
/// Each row is guaranteed to hold face data — no discriminator needed.
/// Uses SQL Server 2025 native VECTOR(512) for cosine similarity search.
/// </summary>
[Table("FaceEmbeddings")]
public class FaceEmbedding
{
    [Key]
    public int Id { get; set; }

    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    // ─── Foreign Key ───

    [Required]
    [Column("PID", TypeName = "varchar(10)")]
    public string PID { get; set; } = string.Empty;

    [ForeignKey(nameof(PID))]
    public Patient Patient { get; set; } = null!;

    // ─── Face Data ───

    /// <summary>
    /// 512-dimensional face embedding from ArcFace.
    /// Stored as SQL Server 2025 native VECTOR(512).
    /// </summary>
    public float[] Embedding { get; set; } = Array.Empty<float>();

    /// <summary>
    /// Small JPEG thumbnail of the cropped face (for UI display).
    /// Typically 112x112 pixels, ~5-10 KB each.
    /// </summary>
    public byte[]? FaceThumbnail { get; set; }

    [Column(TypeName = "varchar(20)")]
    public string? CaptureAngle { get; set; }

    public float? QualityScore { get; set; }

    // ─── Consent & Remark ───

    public bool Consent { get; set; } = true;

    [Column(TypeName = "varchar(100)")]
    public string? Remark { get; set; }

    // ─── Audit ───

    [Column(TypeName = "varchar(50)")]
    public string? CreatedBy { get; set; }

    public DateTime? CreatedDate { get; set; }

    [Column(TypeName = "varchar(50)")]
    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
