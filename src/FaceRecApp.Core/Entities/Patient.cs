using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FaceRecApp.Core.Entities;

/// <summary>
/// Represents a registered patient in the identification system.
/// Maps to the "Patients" table with IDCard as primary key (clustered).
/// </summary>
[Table("Patients")]
public class Patient
{
    [Required]
    [Column(TypeName = "varchar(10)")]
    public string Site { get; set; } = string.Empty;

    /// <summary>
    /// Auto-generated Patient ID (PID). Format: {SiteCode}{5digits}, e.g. "R00001".
    /// Primary key — unique, non-null after enrollment.
    /// </summary>
    [Key]
    [Column(TypeName = "varchar(10)")]
    public string IDCard { get; set; } = string.Empty;

    public DateTime? AdmissionDate { get; set; }

    // ─── Demographics ───

    [Column(TypeName = "varchar(100)")]
    public string? FullName { get; set; }

    [Column(TypeName = "nvarchar(100)")]
    public string? BurmeseName { get; set; }

    [Column(TypeName = "nvarchar(100)")]
    public string? KarenName { get; set; }

    // ─── Family ───

    [Column(TypeName = "varchar(10)")]
    public string? MotherPID { get; set; }

    [Column(TypeName = "varchar(255)")]
    public string? MotherName { get; set; }

    [Column(TypeName = "varchar(255)")]
    public string? FatherName { get; set; }

    [Column(TypeName = "varchar(100)")]
    public string? SpouseName { get; set; }

    // ─── Sex & Age ───

    /// <summary>Sex: 1 = Male, 2 = Female.</summary>
    public byte? Sex { get; set; }

    /// <summary>Age (years) calculated at enrolment from DOB fields.</summary>
    public byte? Age { get; set; }

    /// <summary>Month component of age at enrolment.</summary>
    public byte? Month { get; set; }

    /// <summary>Day component of age at enrolment.</summary>
    public byte? Day { get; set; }

    // ─── Date of Birth ───

    /// <summary>Birth year. Null if unknown.</summary>
    public short? DOB_year { get; set; }

    /// <summary>Birth month (1-12). -1 means "don't know".</summary>
    public short? DOB_month { get; set; }

    /// <summary>Birth day (1-31). -1 means "don't know".</summary>
    public short? DOB_day { get; set; }

    // ─── Address ───

    [Column(TypeName = "varchar(50)")]
    public string? AddressCode { get; set; }

    [Column(TypeName = "varchar(max)")]
    public string? AddressOther { get; set; }

    // ─── Contact ───

    [Column(TypeName = "varchar(50)")]
    public string? PhoneNumber { get; set; }

    // ─── Notes ───

    [Column(TypeName = "varchar(max)")]
    public string? Note { get; set; }

    // ─── Timestamps & Audit ───

    public DateTime? LastModified { get; set; }
    public DateTime? LastSync { get; set; }

    [Column(TypeName = "varchar(50)")]
    public string? CreatedBy { get; set; }

    [Column(TypeName = "varchar(50)")]
    public string? CreatedOn { get; set; }

    [Column(TypeName = "varchar(50)")]
    public string? ModifiedBy { get; set; }

    [Column(TypeName = "varchar(50)")]
    public string? ModifiedOn { get; set; }

    // ─── Navigation ───

    public ICollection<Biometric> Biometrics { get; set; } = new List<Biometric>();
    public ICollection<Visit> Visits { get; set; } = new List<Visit>();
}
