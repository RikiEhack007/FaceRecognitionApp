using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FaceRecApp.Core.Entities;

/// <summary>
/// Represents a patient visit with service routing.
/// Created at Step 4 of the workflow: Visit &amp; Routing.
/// </summary>
public class Visit
{
    [Key]
    public int Id { get; set; }

    // ─── Foreign Key ───

    public int PersonId { get; set; }

    [ForeignKey(nameof(PersonId))]
    public Person Person { get; set; } = null!;

    // ─── Visit Details ───

    public DateTime VisitDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Chief complaint / reason for visit.
    /// </summary>
    [MaxLength(500)]
    public string? ChiefComplaint { get; set; }

    /// <summary>
    /// Service type: OPD, ANC, Vaccine, Study, Follow Up, etc.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string ServiceType { get; set; } = string.Empty;

    // ─── Audit ───

    [MaxLength(50)]
    public string? CreatedBy { get; set; }

    [MaxLength(50)]
    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
