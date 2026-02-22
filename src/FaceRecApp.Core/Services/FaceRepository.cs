using System.Text;
using System.Text.Json;
using FaceRecApp.Core.Data;
using FaceRecApp.Core.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FaceRecApp.Core.Services;

/// <summary>
/// Database repository for face recognition operations.
///
/// This is where the SQL Server 2025 vector magic happens.
///
/// Key operations:
///   - FindClosestMatch(): Uses VECTOR_SEARCH (DiskANN ANN) or
///     VECTOR_DISTANCE (brute-force KNN) depending on whether the
///     DiskANN vector index exists.
///   - RegisterPerson(): Stores a new person with their face embedding(s).
///   - AddFaceSample(): Adds additional face embeddings for better accuracy.
///
/// Thread safety:
///   Uses IDbContextFactory to create short-lived DbContext instances per operation.
///   This is safe for concurrent access from multiple threads.
/// </summary>
public class FaceRepository
{
    private readonly IDbContextFactory<FaceDbContext> _dbFactory;
    private static volatile bool _useVectorSearch;

    public FaceRepository(IDbContextFactory<FaceDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Check if the DiskANN vector index exists and is enabled.
    /// When true, FindClosestMatchAsync uses the faster VECTOR_SEARCH TVF.
    /// Call this at startup or after creating/dropping the index.
    /// </summary>
    public async Task DetectVectorIndexAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var count = await db.Database.SqlQueryRaw<int>(
                @"SELECT COUNT(*) AS [Value] FROM sys.indexes i
                  JOIN sys.tables t ON i.object_id = t.object_id
                  WHERE t.name = 'FaceEmbeddings' AND i.type_desc = 'VECTOR' AND i.is_disabled = 0"
            ).FirstOrDefaultAsync();

            _useVectorSearch = count > 0;
            Console.WriteLine($"[Repository] DiskANN index detected: {_useVectorSearch}");
        }
        catch
        {
            _useVectorSearch = false;
        }
    }

    /// <summary>Whether the DiskANN VECTOR_SEARCH path is active.</summary>
    public bool UseVectorSearch => _useVectorSearch;

    // ══════════════════════════════════════════════
    //  VECTOR SEARCH — The core matching operation
    // ══════════════════════════════════════════════

    /// <summary>
    /// Find the closest matching face embedding in the database.
    ///
    /// Two execution paths:
    ///   1. DiskANN (VECTOR_SEARCH TVF): ~5ms at 100K rows — approximate nearest neighbor
    ///   2. Brute-force (ORDER BY VECTOR_DISTANCE): ~75ms at 100K rows — exact KNN
    ///
    /// The DiskANN path is used automatically when a vector index exists.
    /// Note: DiskANN indexes make the table read-only in SQL Server 2025.
    /// </summary>
    public async Task<FaceMatchResult?> FindClosestMatchAsync(float[] queryEmbedding)
    {
        if (_useVectorSearch)
        {
            try
            {
                return await FindClosestMatchVectorSearchAsync(queryEmbedding);
            }
            catch (SqlException)
            {
                // Index may have been dropped — fall back to brute-force
                _useVectorSearch = false;
            }
        }

        return await FindClosestMatchBruteForceAsync(queryEmbedding);
    }

    /// <summary>
    /// DiskANN approximate nearest neighbor search via VECTOR_SEARCH TVF.
    /// Requires the DiskANN vector index on FaceEmbeddings.Embedding.
    /// </summary>
    private async Task<FaceMatchResult?> FindClosestMatchVectorSearchAsync(float[] queryEmbedding)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var vectorJson = EmbeddingToJson(queryEmbedding);

        // VECTOR_SEARCH returns top N by the DiskANN graph traversal (~5ms at 100K).
        // We request more than 1 because some may belong to inactive persons.
        var results = await db.Database.SqlQueryRaw<VectorSearchRow>(
            @"DECLARE @qv VECTOR(512) = CAST(@p0 AS VECTOR(512));
              SELECT t.Id, t.PersonId, s.distance AS Distance
              FROM VECTOR_SEARCH(
                  TABLE = dbo.FaceEmbeddings AS t,
                  COLUMN = Embedding,
                  SIMILAR_TO = @qv,
                  METRIC = 'cosine',
                  TOP_N = 10
              ) AS s
              ORDER BY s.distance",
            new SqlParameter("@p0", vectorJson)
        ).ToListAsync();

        if (results.Count == 0)
            return null;

        // Join with Persons to filter by IsActive and get person info
        var personIds = results.Select(r => r.PersonId).Distinct().ToList();
        var persons = await db.Persons
            .Where(p => personIds.Contains(p.Id) && p.IsActive)
            .ToDictionaryAsync(p => p.Id);

        foreach (var row in results)
        {
            if (persons.TryGetValue(row.PersonId, out var person))
            {
                return new FaceMatchResult
                {
                    Person = person,
                    FaceEmbedding = new FaceEmbedding { Id = row.Id, PersonId = row.PersonId },
                    Distance = (float)row.Distance,
                    IsMatch = row.Distance <= RecognitionSettings.DistanceThreshold
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Brute-force exact nearest neighbor search via ORDER BY VECTOR_DISTANCE.
    /// Works without any vector index. Scans all rows.
    /// </summary>
    private async Task<FaceMatchResult?> FindClosestMatchBruteForceAsync(float[] queryEmbedding)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var match = await db.FaceEmbeddings
            .Include(e => e.Person)
            .Where(e => e.Person.IsActive)
            .Select(e => new
            {
                Embedding = e,
                Distance = EF.Functions.VectorDistance("cosine", e.Embedding, queryEmbedding)
            })
            .OrderBy(x => x.Distance)
            .FirstOrDefaultAsync();

        if (match == null)
            return null;

        return new FaceMatchResult
        {
            Person = match.Embedding.Person,
            FaceEmbedding = match.Embedding,
            Distance = (float)match.Distance,
            IsMatch = match.Distance <= RecognitionSettings.DistanceThreshold
        };
    }

    private static string EmbeddingToJson(float[] embedding)
    {
        var sb = new StringBuilder(embedding.Length * 12);
        sb.Append('[');
        for (int i = 0; i < embedding.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(embedding[i].ToString("G9"));
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Find the top N closest matches (for debugging or when multiple people look similar).
    /// </summary>
    public async Task<List<FaceMatchResult>> FindTopMatchesAsync(float[] queryEmbedding, int topN = 5)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var matches = await db.FaceEmbeddings
            .Include(e => e.Person)
            .Where(e => e.Person.IsActive)
            .Select(e => new
            {
                Embedding = e,
                Distance = EF.Functions.VectorDistance("cosine", e.Embedding, queryEmbedding)
            })
            .OrderBy(x => x.Distance)
            .Take(topN)
            .ToListAsync();

        return matches.Select(m => new FaceMatchResult
        {
            Person = m.Embedding.Person,
            FaceEmbedding = m.Embedding,
            Distance = (float)m.Distance,
            IsMatch = m.Distance <= RecognitionSettings.DistanceThreshold
        }).ToList();
    }

    // ══════════════════════════════════════════════
    //  REGISTRATION — Enroll a new person
    // ══════════════════════════════════════════════

    /// <summary>
    /// Register a new person with their initial face embedding(s).
    /// 
    /// Typical registration flow:
    ///   1. Capture 3-5 face images from different angles
    ///   2. Generate an embedding for each image
    ///   3. Call RegisterPersonAsync() with all embeddings
    ///   4. Person is now searchable via FindClosestMatchAsync()
    /// </summary>
    /// <param name="name">Person's display name</param>
    /// <param name="embeddings">One or more face embeddings (recommended: 3-5)</param>
    /// <param name="thumbnails">Optional JPEG thumbnails matching each embedding</param>
    /// <param name="externalId">Optional external system reference (e.g., patient ID)</param>
    /// <param name="notes">Optional notes</param>
    /// <returns>The created Person entity</returns>
    public async Task<Person> RegisterPersonAsync(
        string name,
        IReadOnlyList<float[]> embeddings,
        IReadOnlyList<byte[]?>? thumbnails = null,
        string? externalId = null,
        string? notes = null)
    {
        if (embeddings.Count == 0)
            throw new ArgumentException("At least one face embedding is required");

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Check for duplicate external ID
        if (!string.IsNullOrEmpty(externalId))
        {
            var existing = await db.Persons
                .FirstOrDefaultAsync(p => p.ExternalId == externalId);
            if (existing != null)
                throw new InvalidOperationException(
                    $"A person with external ID '{externalId}' already exists: {existing.Name}");
        }

        var person = new Person
        {
            Name = name,
            ExternalId = externalId,
            Notes = notes,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            IsActive = true
        };

        // Add face embeddings
        for (int i = 0; i < embeddings.Count; i++)
        {
            var faceEmbedding = new FaceEmbedding
            {
                Embedding = embeddings[i],
                FaceThumbnail = thumbnails != null && i < thumbnails.Count ? thumbnails[i] : null,
                CapturedAt = DateTime.UtcNow,
                CaptureAngle = i switch
                {
                    0 => "front",
                    1 => "left",
                    2 => "right",
                    _ => $"sample_{i + 1}"
                }
            };
            person.FaceEmbeddings.Add(faceEmbedding);
        }

        db.Persons.Add(person);
        await db.SaveChangesAsync();

        return person;
    }

    /// <summary>
    /// Quick registration with a single embedding.
    /// Convenience method for the PoC — in production, use multiple samples.
    /// </summary>
    public async Task<Person> RegisterPersonAsync(
        string name,
        float[] embedding,
        byte[]? thumbnail = null,
        string? notes = null)
    {
        return await RegisterPersonAsync(
            name,
            new[] { embedding },
            thumbnail != null ? new[] { thumbnail } : null,
            notes: notes);
    }

    // ══════════════════════════════════════════════
    //  ENROLLMENT — Add more face samples
    // ══════════════════════════════════════════════

    /// <summary>
    /// Add an additional face sample to an existing person.
    /// More samples from different angles improves recognition accuracy.
    /// </summary>
    public async Task<FaceEmbedding> AddFaceSampleAsync(
        int personId,
        float[] embedding,
        byte[]? thumbnail = null,
        string? angle = null)
    {

        await using var db = await _dbFactory.CreateDbContextAsync();

        var person = await db.Persons.FindAsync(personId)
            ?? throw new ArgumentException($"Person with ID {personId} not found");

        var faceEmbedding = new FaceEmbedding
        {
            PersonId = personId,
            Embedding = embedding,
            FaceThumbnail = thumbnail,
            CaptureAngle = angle,
            CapturedAt = DateTime.UtcNow
        };

        db.FaceEmbeddings.Add(faceEmbedding);
        await db.SaveChangesAsync();

        return faceEmbedding;
    }

    // ══════════════════════════════════════════════
    //  RECOGNITION LOGGING
    // ══════════════════════════════════════════════

    /// <summary>
    /// Log a recognition attempt (both successful and failed).
    /// Used for audit trail and analytics.
    /// </summary>
    public async Task LogRecognitionAsync(
        int? personId,
        float distance,
        bool wasRecognized,
        bool passedLiveness,
        string? stationId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var log = new RecognitionLog
        {
            PersonId = personId,
            Distance = distance,
            WasRecognized = wasRecognized,
            PassedLiveness = passedLiveness,
            StationId = stationId,
            Timestamp = DateTime.UtcNow
        };

        db.RecognitionLogs.Add(log);

        // Update the person's last seen time and recognition count
        if (wasRecognized && personId.HasValue)
        {
            var person = await db.Persons.FindAsync(personId.Value);
            if (person != null)
            {
                person.LastSeenAt = DateTime.UtcNow;
                person.TotalRecognitions++;
            }
        }

        await db.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════
    //  PERSON MANAGEMENT (CRUD)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Get all active persons with their face sample counts.
    /// </summary>
    public async Task<List<PersonSummary>> GetAllPersonsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Persons
            .Where(p => p.IsActive)
            .Select(p => new PersonSummary
            {
                Id = p.Id,
                Name = p.Name,
                ExternalId = p.ExternalId,
                Notes = p.Notes,
                FaceSampleCount = p.FaceEmbeddings.Count,
                TotalRecognitions = p.TotalRecognitions,
                LastSeenAt = p.LastSeenAt,
                CreatedAt = p.CreatedAt
            })
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Get a person with all their face embeddings.
    /// </summary>
    public async Task<Person?> GetPersonWithEmbeddingsAsync(int personId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Persons
            .Include(p => p.FaceEmbeddings)
            .FirstOrDefaultAsync(p => p.Id == personId);
    }

    /// <summary>
    /// Soft-delete a person (set IsActive = false).
    /// Their face embeddings are kept but excluded from searches.
    /// </summary>
    public async Task DeactivatePersonAsync(int personId)
    {

        await using var db = await _dbFactory.CreateDbContextAsync();

        var person = await db.Persons.FindAsync(personId);
        if (person != null)
        {
            person.IsActive = false;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Hard-delete a person and all their face embeddings.
    /// </summary>
    public async Task DeletePersonAsync(int personId)
    {

        await using var db = await _dbFactory.CreateDbContextAsync();

        var person = await db.Persons
            .Include(p => p.FaceEmbeddings)
            .FirstOrDefaultAsync(p => p.Id == personId);

        if (person != null)
        {
            db.Persons.Remove(person); // Cascade deletes embeddings
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Delete a specific face sample.
    /// </summary>
    public async Task DeleteFaceSampleAsync(int embeddingId)
    {

        await using var db = await _dbFactory.CreateDbContextAsync();

        var embedding = await db.FaceEmbeddings.FindAsync(embeddingId);
        if (embedding != null)
        {
            db.FaceEmbeddings.Remove(embedding);
            await db.SaveChangesAsync();
        }
    }

    // ══════════════════════════════════════════════
    //  STATISTICS
    // ══════════════════════════════════════════════

    /// <summary>
    /// Get database statistics for the dashboard.
    /// </summary>
    public async Task<DatabaseStats> GetStatsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var stats = new DatabaseStats
        {
            TotalPersons = await db.Persons.CountAsync(p => p.IsActive),
            TotalEmbeddings = await db.FaceEmbeddings.CountAsync(),
            TotalRecognitions = await db.RecognitionLogs.CountAsync(),
            SuccessfulRecognitions = await db.RecognitionLogs.CountAsync(r => r.WasRecognized),
            LivenessFailures = await db.RecognitionLogs.CountAsync(r => !r.PassedLiveness),
        };

        // Average samples per person
        if (stats.TotalPersons > 0)
        {
            stats.AverageSamplesPerPerson =
                (float)stats.TotalEmbeddings / stats.TotalPersons;
        }

        // Recognition rate
        if (stats.TotalRecognitions > 0)
        {
            stats.RecognitionRate =
                (float)stats.SuccessfulRecognitions / stats.TotalRecognitions * 100f;
        }

        return stats;
    }
}

// ══════════════════════════════════════════════
//  DTOs (Data Transfer Objects)
// ══════════════════════════════════════════════

/// <summary>
/// Result of a vector similarity search.
/// </summary>
public class FaceMatchResult
{
    public Person Person { get; set; } = null!;
    public FaceEmbedding FaceEmbedding { get; set; } = null!;

    /// <summary>
    /// Cosine distance (0.0 = identical, 1.0 = completely different).
    /// </summary>
    public float Distance { get; set; }

    /// <summary>
    /// Is this distance below the recognition threshold?
    /// </summary>
    public bool IsMatch { get; set; }

    /// <summary>
    /// Cosine similarity (1 - Distance). Higher = better match.
    /// </summary>
    public float Similarity => 1f - Distance;

    public string SimilarityText => $"{Similarity * 100:F1}%";

    public bool IsHighConfidence =>
        IsMatch && Distance <= RecognitionSettings.HighConfidenceDistance;
}

/// <summary>
/// Summary view of a person (without embedding data).
/// </summary>
public class PersonSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ExternalId { get; set; }
    public string? Notes { get; set; }
    public int FaceSampleCount { get; set; }
    public int TotalRecognitions { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Database statistics for the dashboard.
/// </summary>
public class DatabaseStats
{
    public int TotalPersons { get; set; }
    public int TotalEmbeddings { get; set; }
    public int TotalRecognitions { get; set; }
    public int SuccessfulRecognitions { get; set; }
    public int LivenessFailures { get; set; }
    public float AverageSamplesPerPerson { get; set; }
    public float RecognitionRate { get; set; }
}

/// <summary>
/// Row returned by VECTOR_SEARCH TVF raw SQL query.
/// </summary>
public class VectorSearchRow
{
    public int Id { get; set; }
    public int PersonId { get; set; }
    public double Distance { get; set; }
}
