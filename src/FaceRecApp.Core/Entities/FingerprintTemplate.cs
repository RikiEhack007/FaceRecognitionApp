using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FaceRecApp.Core.Entities;

/// <summary>
/// Stores a fingerprint enrollment template for a patient.
/// One record per finger (FingerL1–L5, FingerR1–R5).
/// Template is nullable — NULL with Remark set means capture failed.
/// </summary>
public class FingerprintTemplate
{
    [Key]
    public int Id { get; set; }

    // ─── FK to Patient (Cascade delete) ───

    public int PersonId { get; set; }

    [ForeignKey(nameof(PersonId))]
    public Person Person { get; set; } = null!;

    // ─── Fingerprint Data ───

    /// <summary>
    /// Which finger: "FingerL1"–"FingerL5", "FingerR1"–"FingerR5".
    /// Uses constants from BiometricRemarks.Types.
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string FingerType { get; set; } = "FingerR2";

    /// <summary>
    /// Merged enrollment template from ZK SDK (DBMerge of 3 captures).
    /// NULL when capture failed — see Remark for reason.
    /// </summary>
    public byte[]? Template { get; set; }

    // ─── Consent & Audit ───

    public DateTime CaptureDate { get; set; } = DateTime.UtcNow;

    public bool Consent { get; set; }

    /// <summary>
    /// Reason capture failed (e.g., "Physical Deformity", "Equipment Issue").
    /// NULL when capture succeeded.
    /// </summary>
    [MaxLength(100)]
    public string? Remark { get; set; }

    [MaxLength(500)]
    public string? ConsentRefusalReason { get; set; }

    [MaxLength(50)]
    public string? CreatedBy { get; set; }

    [MaxLength(50)]
    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
