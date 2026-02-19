using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FaceRecApp.Core.Entities;

/// <summary>
/// Stores a single face embedding (512-dimensional vector from ArcFace model).
/// 
/// How it works:
///   1. Camera captures a face image
///   2. FaceAiSharp detects the face and extracts landmarks
///   3. ArcFace model converts the aligned face into a float[512] vector
///   4. This vector is stored in SQL Server as native VECTOR(512)
///   5. To recognize someone, we generate a new vector and use
///      VECTOR_DISTANCE('cosine', stored, query) to find the closest match
///
/// Storage math:
///   - 512 floats × 4 bytes = 2,048 bytes per embedding
///   - 10,000 people × 5 samples each = 50,000 embeddings ≈ 100 MB
///   - SQL Server Express limit: 50 GB → more than enough for PoC
/// </summary>
public class FaceEmbedding
{
    [Key]
    public int Id { get; set; }

    // ─── Foreign Key ───
    public int PersonId { get; set; }

    [ForeignKey(nameof(PersonId))]
    public Person Person { get; set; } = null!;

    // ─── The Vector ───
    /// <summary>
    /// 512-dimensional face embedding from ArcFace.
    /// 
    /// In the database, this is stored as VECTOR(512) — a native SQL Server 2025 type.
    /// EF Core maps float[] → VECTOR(512) via the VectorSearch plugin.
    /// 
    /// Cosine similarity is computed by SQL Server:
    ///   VECTOR_DISTANCE('cosine', Embedding, @queryVector)
    ///   Returns: 0.0 = identical, 1.0 = completely different
    /// </summary>
    public float[] Embedding { get; set; } = Array.Empty<float>();

    // ─── Metadata ───
    /// <summary>
    /// Small JPEG thumbnail of the cropped face (for UI display).
    /// Typically 112×112 pixels, ~5-10 KB each.
    /// </summary>
    public byte[]? FaceThumbnail { get; set; }

    /// <summary>
    /// Which angle the face was captured from.
    /// Helps ensure diversity of enrollment samples.
    /// </summary>
    [MaxLength(20)]
    public string? CaptureAngle { get; set; }  // "front", "left", "right", "up", "down"

    /// <summary>
    /// Quality score of the face image (0.0 - 1.0).
    /// Lower quality = less reliable embedding.
    /// </summary>
    public float? QualityScore { get; set; }

    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
}
