using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FaceRecApp.Core.Entities;

/// <summary>
/// Represents a registered patient in the identification system.
/// Maps to the "Patients" table. One patient can have multiple face embeddings,
/// biometric records, and visits.
/// </summary>
[Table("Patients")]
public class Person
{
    [Key]
    public int Id { get; set; }

    // ─── Patient Identification ───

    /// <summary>
    /// Site code for this patient's registration (e.g., "R" for the primary site).
    /// </summary>
    [MaxLength(10)]
    public string? Site { get; set; }

    /// <summary>
    /// Auto-generated Patient ID (PID). Format: {SiteCode}{5digits}, e.g. "R00001".
    /// Primary business identifier — unique, non-null after enrollment.
    /// </summary>
    [Required]
    [MaxLength(10)]
    public string IDCard { get; set; } = string.Empty;

    /// <summary>
    /// Date when the patient was first enrolled in the system.
    /// </summary>
    public DateTime? AdmissionDate { get; set; }

    // ─── Demographics ───

    /// <summary>
    /// Patient full name (replaces former 'Name' field).
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// Sex: 1 = Male, 2 = Female.
    /// </summary>
    public byte? Sex { get; set; }

    /// <summary>
    /// Birth year. Null if unknown.
    /// </summary>
    public short? DOBYear { get; set; }

    /// <summary>
    /// Birth month (1-12). -1 means "don't know".
    /// </summary>
    public short? DOBMonth { get; set; }

    /// <summary>
    /// Birth day (1-31). -1 means "don't know".
    /// </summary>
    public short? DOBDay { get; set; }

    /// <summary>
    /// Age (years) calculated at enrolment from DOB fields.
    /// </summary>
    public byte? AgeAtEnrolment { get; set; }

    /// <summary>
    /// Month component of age at enrolment.
    /// </summary>
    public byte? MonthAtEnrolment { get; set; }

    /// <summary>
    /// Day component of age at enrolment.
    /// </summary>
    public byte? DayAtEnrolment { get; set; }

    // ─── Address ───

    [MaxLength(50)]
    public string? AddressCode { get; set; }

    public string? AddressOther { get; set; }

    // ─── Family ───

    /// <summary>
    /// PID of the patient's mother (FK to another Patient, nullable).
    /// </summary>
    [MaxLength(10)]
    public string? MotherPID { get; set; }

    [MaxLength(255)]
    public string? MotherName { get; set; }

    [MaxLength(255)]
    public string? FatherName { get; set; }

    [MaxLength(100)]
    public string? SpouseName { get; set; }

    // ─── Consent ───

    /// <summary>
    /// Whether the patient gave e-consent for biometric data collection.
    /// </summary>
    public bool ConsentGiven { get; set; }

    public DateTime? ConsentDate { get; set; }

    // ─── Notes & Legacy ───

    /// <summary>
    /// Optional notes (department, role, etc.).
    /// </summary>
    [MaxLength(500)]
    public string? Notes { get; set; }

    /// <summary>
    /// External reference ID — for linking to external systems (HIS, EMR).
    /// </summary>
    [MaxLength(50)]
    public string? ExternalId { get; set; }

    // ─── Timestamps & Audit ───

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public int TotalRecognitions { get; set; } = 0;
    public bool IsActive { get; set; } = true;

    [MaxLength(50)]
    public string? CreatedBy { get; set; }

    [MaxLength(50)]
    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }
    public DateTime? LastSync { get; set; }

    // ─── Navigation ───

    public ICollection<FaceEmbedding> FaceEmbeddings { get; set; } = new List<FaceEmbedding>();
    public ICollection<FingerprintTemplate> FingerprintTemplates { get; set; } = new List<FingerprintTemplate>();
    public ICollection<Visit> Visits { get; set; } = new List<Visit>();
}
