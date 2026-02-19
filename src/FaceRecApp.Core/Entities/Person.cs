using System.ComponentModel.DataAnnotations;

namespace FaceRecApp.Core.Entities;

/// <summary>
/// Represents a registered person in the face recognition system.
/// One person can have multiple face embeddings (different angles, lighting).
/// </summary>
public class Person
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Display name (e.g., patient name, employee name).
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional notes (department, role, patient ID, etc.).
    /// </summary>
    [MaxLength(500)]
    public string? Notes { get; set; }

    /// <summary>
    /// External reference ID — for linking to external systems (HIS, EMR).
    /// Example: Patient ID "P-20260001" from the hospital system.
    /// </summary>
    [MaxLength(50)]
    public string? ExternalId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Total number of successful recognitions (for analytics).
    /// </summary>
    public int TotalRecognitions { get; set; } = 0;

    public bool IsActive { get; set; } = true;

    // ─── Navigation ───
    /// <summary>
    /// A person can have multiple face embeddings (recommended: 3-5).
    /// More samples from different angles = better recognition accuracy.
    /// </summary>
    public ICollection<FaceEmbedding> FaceEmbeddings { get; set; } = new List<FaceEmbedding>();
}
