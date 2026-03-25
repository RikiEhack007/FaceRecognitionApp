using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FaceRecApp.Core.Entities;

/// <summary>
/// Stores a fingerprint enrollment template for a patient.
/// One record per finger (FingerL1-L5, FingerR1-R5).
/// Template is nullable — NULL with Remark set means capture failed.
/// Uses ZK SDK's in-memory 1:N matching (not vector search).
/// </summary>
[Table("FingerprintTemplates")]
public class FingerprintTemplate
{
    [Key]
    public int Id { get; set; }

    // ─── Foreign Key ───

    [Required]
    [Column("PID", TypeName = "varchar(10)")]
    public string PID { get; set; } = string.Empty;

    [ForeignKey(nameof(PID))]
    public Patient Patient { get; set; } = null!;

    // ─── Fingerprint Data ───

    /// <summary>
    /// Which finger: "FingerL1"-"FingerL5", "FingerR1"-"FingerR5".
    /// Uses constants from BiometricRemarks.Types.
    /// </summary>
    [Required]
    [Column(TypeName = "varchar(20)")]
    public string FingerType { get; set; } = string.Empty;

    /// <summary>
    /// Merged enrollment template from ZK SDK (DBMerge of 3 captures).
    /// NULL when capture failed — see Remark for reason.
    /// </summary>
    public byte[]? Template { get; set; }

    public DateTime CaptureDate { get; set; } = DateTime.UtcNow;

    // ─── Consent ───

    public bool Consent { get; set; } = true;

    [Column(TypeName = "varchar(500)")]
    public string? ConsentRefusalReason { get; set; }

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
