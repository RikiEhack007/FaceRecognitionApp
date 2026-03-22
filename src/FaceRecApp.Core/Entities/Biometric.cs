using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FaceRecApp.Core.Entities;

/// <summary>
/// Unified biometric record for both face embeddings and fingerprint templates.
///
/// BiometricType discriminates the data:
///   - "Face": Embedding (vector(512)) is populated, Template is null
///   - "FingerL1"-"FingerL5", "FingerR1"-"FingerR5": Template (varbinary) is populated, Embedding is null
///
/// Face and fingerprint data are used separately because fingerprint templates (SDK binary)
/// and face embeddings (512-dim float vector) cannot be combined.
/// Face uses SQL Server VECTOR_DISTANCE for cosine similarity search.
/// Fingerprint uses the ZK SDK's in-memory 1:N matching.
/// </summary>
[Table("Biometrics")]
public class Biometric
{
    [Key]
    public int Id { get; set; }

    public DateTime Date { get; set; } = DateTime.UtcNow;

    // ─── Foreign Key ───

    [Required]
    [Column("PID", TypeName = "varchar(10)")]
    public string PID { get; set; } = string.Empty;

    [ForeignKey(nameof(PID))]
    public Patient Patient { get; set; } = null!;

    // ─── Biometric Type ───

    /// <summary>
    /// Discriminator: "Face", "FingerL1"-"FingerL5", "FingerR1"-"FingerR5".
    /// Uses constants from BiometricRemarks.Types.
    /// </summary>
    [Required]
    [Column(TypeName = "varchar(20)")]
    public string BiometricType { get; set; } = string.Empty;

    // ─── Face Data (populated when BiometricType = "Face") ───

    /// <summary>
    /// 512-dimensional face embedding from ArcFace.
    /// Stored as SQL Server 2025 native VECTOR(512).
    /// Null for fingerprint records.
    /// </summary>
    public float[]? Embedding { get; set; }

    /// <summary>
    /// Small JPEG thumbnail of the cropped face (for UI display).
    /// Typically 112x112 pixels, ~5-10 KB each. Null for fingerprint records.
    /// </summary>
    public byte[]? FaceThumbnail { get; set; }

    /// <summary>
    /// Which angle the face was captured from.
    /// Null for fingerprint records.
    /// </summary>
    [Column(TypeName = "varchar(20)")]
    public string? CaptureAngle { get; set; }

    /// <summary>
    /// Quality score of the face image (0.0 - 1.0). Null for fingerprint records.
    /// </summary>
    public float? QualityScore { get; set; }

    // ─── Fingerprint Data (populated when BiometricType = "Finger*") ───

    /// <summary>
    /// Merged enrollment template from ZK SDK (DBMerge of 3 captures).
    /// Null for face records, or when fingerprint capture failed (see Remark).
    /// </summary>
    public byte[]? Template { get; set; }

    // ─── Remark ───

    /// <summary>
    /// Reason biometric couldn't be captured (e.g., "Physical Deformity", "Equipment Issue").
    /// Null when capture succeeded. See BiometricRemarks for available options.
    /// </summary>
    [Column(TypeName = "varchar(100)")]
    public string? Remark { get; set; }

    // ─── Consent & Audit ───

    public bool Consent { get; set; } = true;

    [Column(TypeName = "varchar(500)")]
    public string? ConsentRefusalReason { get; set; }

    [Column(TypeName = "varchar(50)")]
    public string? CreatedBy { get; set; }

    public DateTime? CreatedDate { get; set; }

    [Column(TypeName = "varchar(50)")]
    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    // ─── Helpers ───

    [NotMapped]
    public bool IsFace => BiometricType == BiometricRemarks.Types.Face;

    [NotMapped]
    public bool IsFingerprint => BiometricType != BiometricRemarks.Types.Face;
}
