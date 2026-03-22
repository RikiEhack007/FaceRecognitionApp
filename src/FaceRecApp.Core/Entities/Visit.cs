using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FaceRecApp.Core.Entities;

/// <summary>
/// Represents a patient visit with service routing.
/// </summary>
[Table("Visits")]
public class Visit
{
    [Key]
    public int Id { get; set; }

    // ─── Foreign Key ───

    [Required]
    [Column("PID", TypeName = "varchar(10)")]
    public string PID { get; set; } = string.Empty;

    [ForeignKey(nameof(PID))]
    public Patient Patient { get; set; } = null!;

    // ─── Visit Details ───

    public DateTime Date { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Chief complaint / reason for visit.
    /// </summary>
    [Column("CC", TypeName = "varchar(500)")]
    public string? ChiefComplaint { get; set; }

    /// <summary>
    /// Service type: OPD, ANC, Vaccine, Study, Follow Up, etc.
    /// </summary>
    [Required]
    [Column(TypeName = "varchar(50)")]
    public string ServiceType { get; set; } = string.Empty;

    // ─── Audit ───

    [Column(TypeName = "varchar(50)")]
    public string? CreatedBy { get; set; }

    public DateTime? CreatedDate { get; set; }

    [Column(TypeName = "varchar(50)")]
    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
