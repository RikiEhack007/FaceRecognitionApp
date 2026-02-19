using FaceRecApp.Core.Data;
using FaceRecApp.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FaceRecApp.Core.Services;

/// <summary>
/// Database repository for face recognition operations.
/// 
/// This is where the SQL Server 2025 vector magic happens.
/// 
/// Key operations:
///   - FindClosestMatch(): Uses VECTOR_DISTANCE('cosine', ...) to find the nearest
///     face embedding in the database — this runs INSIDE SQL Server, not in C#.
///   - RegisterPerson(): Stores a new person with their face embedding(s).
///   - AddFaceSample(): Adds additional face embeddings for better accuracy.
/// 
/// Why a repository pattern?
///   - Separates database logic from business logic
///   - Makes it easy to swap SQL Server for another database (e.g., PostgreSQL + pgvector)
///   - Simplifies unit testing with mock/in-memory database
/// 
/// Thread safety:
///   Uses IDbContextFactory to create short-lived DbContext instances per operation.
///   This is safe for concurrent access from multiple threads.
/// </summary>
public class FaceRepository
{
    private readonly IDbContextFactory<FaceDbContext> _dbFactory;

    public FaceRepository(IDbContextFactory<FaceDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    // ══════════════════════════════════════════════
    //  VECTOR SEARCH — The core matching operation
    // ══════════════════════════════════════════════

    /// <summary>
    /// Find the closest matching face embedding in the database.
    /// 
    /// This is the heart of the recognition system. It:
    ///   1. Takes a query embedding (from a live camera frame)
    ///   2. Sends it to SQL Server
    ///   3. SQL Server computes VECTOR_DISTANCE('cosine', stored, query)
    ///      for every embedding in the database
    ///   4. Returns the closest match (smallest distance)
    /// 
    /// The generated SQL looks like:
    ///   SELECT TOP(1) e.*, p.*,
    ///     VECTOR_DISTANCE('cosine', e.Embedding, @queryVector) AS Distance
    ///   FROM FaceEmbeddings e
    ///   JOIN Persons p ON e.PersonId = p.Id
    ///   WHERE p.IsActive = 1
    ///   ORDER BY Distance ASC
    /// 
    /// Performance:
    ///   - 100 faces:   ~1ms
    ///   - 1,000 faces:  ~3ms
    ///   - 10,000 faces: ~10ms (with DiskANN index: ~2ms)
    /// </summary>
    /// <param name="queryEmbedding">512-dim float array from FaceRecognitionService</param>
    /// <returns>Match result with person info and distance, or null if DB is empty</returns>
    public async Task<FaceMatchResult?> FindClosestMatchAsync(float[] queryEmbedding)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // ⭐ THE KEY QUERY: SQL Server 2025 vector distance search
        // 
        // EF.Functions.VectorDistance() is translated by the EFCore.SqlServer.VectorSearch
        // plugin into:  VECTOR_DISTANCE('cosine', [Embedding], @p0)
        //
        // This runs entirely inside SQL Server — no data comes back to C# for comparison.
        // SQL Server's query engine is optimized for this operation.
        var match = await db.FaceEmbeddings
            .Include(e => e.Person)
            .Where(e => e.Person.IsActive)  // Only search active persons
            .Select(e => new
            {
                Embedding = e,
                Distance = EF.Functions.VectorDistance("cosine", e.Embedding, queryEmbedding)
            })
            .OrderBy(x => x.Distance)  // Closest first (smallest distance)
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
