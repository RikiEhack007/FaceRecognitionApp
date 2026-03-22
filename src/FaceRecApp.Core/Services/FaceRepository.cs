using System.Text;
using FaceRecApp.Core.Data;
using FaceRecApp.Core.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FaceRecApp.Core.Services;

/// <summary>
/// Database repository for face recognition operations.
///
/// Key operations:
///   - FindClosestMatch(): Uses VECTOR_SEARCH (DiskANN ANN) or
///     VECTOR_DISTANCE (brute-force KNN) depending on index.
///   - RegisterPatient(): Stores a new patient with biometrics.
///
/// Thread safety:
///   Uses IDbContextFactory to create short-lived DbContext instances per operation.
/// </summary>
public class FaceRepository
{
    private readonly IDbContextFactory<FaceDbContext> _dbFactory;
    private static volatile bool _useVectorSearch;

    /// <summary>Shared projection from Patient → PatientSummary (translatable by EF Core).</summary>
    private static readonly System.Linq.Expressions.Expression<Func<Patient, PatientSummary>> ToSummary =
        p => new PatientSummary
        {
            IDCard = p.IDCard,
            Name = p.FullName ?? "",
            Site = p.Site,
            Sex = p.Sex,
            Note = p.Note,
            FaceSampleCount = p.Biometrics.Count(b => b.BiometricType == BiometricRemarks.Types.Face),
            CreatedOn = p.CreatedOn,
        };

    public FaceRepository(IDbContextFactory<FaceDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Check if the DiskANN vector index exists and is enabled.
    /// </summary>
    public async Task DetectVectorIndexAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var count = await db.Database.SqlQueryRaw<int>(
                @"SELECT COUNT(*) AS [Value] FROM sys.indexes i
                  JOIN sys.tables t ON i.object_id = t.object_id
                  WHERE t.name = 'Biometrics' AND i.type_desc = 'VECTOR' AND i.is_disabled = 0"
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
    /// Two paths: DiskANN (~5ms) or brute-force (~75ms).
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
                _useVectorSearch = false;
            }
        }

        return await FindClosestMatchBruteForceAsync(queryEmbedding);
    }

    private async Task<FaceMatchResult?> FindClosestMatchVectorSearchAsync(float[] queryEmbedding)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var vectorJson = EmbeddingToJson(queryEmbedding);

        var results = await db.Database.SqlQueryRaw<VectorSearchRow>(
            @"DECLARE @qv VECTOR(512) = CAST(@p0 AS VECTOR(512));
              SELECT t.Id, t.PID, s.distance AS Distance
              FROM VECTOR_SEARCH(
                  TABLE = dbo.Biometrics AS t,
                  COLUMN = Embedding,
                  SIMILAR_TO = @qv,
                  METRIC = 'cosine',
                  TOP_N = 10
              ) AS s
              WHERE t.BiometricType = 'Face'
              ORDER BY s.distance",
            new SqlParameter("@p0", vectorJson)
        ).ToListAsync();

        if (results.Count == 0)
            return null;

        // Join with Patients to get person info
        var pids = results.Select(r => r.PID).Distinct().ToList();
        var patients = await db.Patients
            .Where(p => pids.Contains(p.IDCard))
            .ToDictionaryAsync(p => p.IDCard);

        foreach (var row in results)
        {
            if (patients.TryGetValue(row.PID, out var patient))
            {
                return new FaceMatchResult
                {
                    Patient = patient,
                    BiometricId = row.Id,
                    Distance = (float)row.Distance,
                    IsMatch = row.Distance <= RecognitionSettings.DistanceThreshold
                };
            }
        }

        return null;
    }

    private async Task<FaceMatchResult?> FindClosestMatchBruteForceAsync(float[] queryEmbedding)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var match = await db.Biometrics
            .Include(b => b.Patient)
            .Where(b => b.BiometricType == BiometricRemarks.Types.Face && b.Embedding != null)
            .Select(b => new
            {
                Biometric = b,
                Distance = EF.Functions.VectorDistance("cosine", b.Embedding!, queryEmbedding)
            })
            .OrderBy(x => x.Distance)
            .FirstOrDefaultAsync();

        if (match == null)
            return null;

        return new FaceMatchResult
        {
            Patient = match.Biometric.Patient,
            BiometricId = match.Biometric.Id,
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
    /// Find the top N closest matches.
    /// </summary>
    public async Task<List<FaceMatchResult>> FindTopMatchesAsync(float[] queryEmbedding, int topN = 5)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var matches = await db.Biometrics
            .Include(b => b.Patient)
            .Where(b => b.BiometricType == BiometricRemarks.Types.Face && b.Embedding != null)
            .Select(b => new
            {
                Biometric = b,
                Distance = EF.Functions.VectorDistance("cosine", b.Embedding!, queryEmbedding)
            })
            .OrderBy(x => x.Distance)
            .Take(topN)
            .ToListAsync();

        return matches.Select(m => new FaceMatchResult
        {
            Patient = m.Biometric.Patient,
            BiometricId = m.Biometric.Id,
            Distance = (float)m.Distance,
            IsMatch = m.Distance <= RecognitionSettings.DistanceThreshold
        }).ToList();
    }

    // ══════════════════════════════════════════════
    //  VERIFY — 1:1 comparison against a specific patient
    // ══════════════════════════════════════════════

    /// <summary>
    /// 1:1 verification: compare a query embedding against a specific patient's face embeddings.
    /// </summary>
    public async Task<FaceMatchResult?> VerifyAgainstPatientAsync(string pid, float[] queryEmbedding)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var match = await db.Biometrics
            .Include(b => b.Patient)
            .Where(b => b.PID == pid
                     && b.BiometricType == BiometricRemarks.Types.Face
                     && b.Embedding != null)
            .Select(b => new
            {
                Biometric = b,
                Distance = EF.Functions.VectorDistance("cosine", b.Embedding!, queryEmbedding)
            })
            .OrderBy(x => x.Distance)
            .FirstOrDefaultAsync();

        if (match == null)
            return null;

        return new FaceMatchResult
        {
            Patient = match.Biometric.Patient,
            BiometricId = match.Biometric.Id,
            Distance = (float)match.Distance,
            IsMatch = match.Distance <= RecognitionSettings.DistanceThreshold
        };
    }

    /// <summary>Create a face Biometric record with standard audit fields.</summary>
    private static Biometric CreateFaceBiometric(float[] embedding, byte[]? thumbnail, string? angle = "front")
        => new()
        {
            BiometricType = BiometricRemarks.Types.Face,
            Embedding = embedding,
            FaceThumbnail = thumbnail,
            CaptureAngle = angle,
            Date = DateTime.UtcNow,
            Consent = true,
            CreatedBy = Environment.UserName,
            CreatedDate = DateTime.UtcNow,
        };

    // ══════════════════════════════════════════════
    //  REGISTRATION
    // ══════════════════════════════════════════════

    /// <summary>
    /// Register a new person with their initial face embedding(s).
    /// </summary>
    public async Task<Patient> RegisterPatientAsync(
        string name,
        IReadOnlyList<float[]> embeddings,
        IReadOnlyList<byte[]?>? thumbnails = null,
        string? notes = null)
    {
        if (embeddings.Count == 0)
            throw new ArgumentException("At least one face embedding is required");

        await using var db = await _dbFactory.CreateDbContextAsync();

        var patient = new Patient
        {
            FullName = name,
            IDCard = string.Empty,
            Site = string.Empty,
            Note = notes,
            CreatedOn = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            CreatedBy = Environment.UserName,
        };

        for (int i = 0; i < embeddings.Count; i++)
        {
            var thumb = thumbnails != null && i < thumbnails.Count ? thumbnails[i] : null;
            var angle = i switch { 0 => "front", 1 => "left", 2 => "right", _ => $"sample_{i + 1}" };
            patient.Biometrics.Add(CreateFaceBiometric(embeddings[i], thumb, angle));
        }

        db.Patients.Add(patient);
        await db.SaveChangesAsync();

        return patient;
    }

    /// <summary>
    /// Quick registration with a single embedding.
    /// </summary>
    public async Task<Patient> RegisterPatientAsync(
        string name,
        float[] embedding,
        byte[]? thumbnail = null,
        string? notes = null)
    {
        return await RegisterPatientAsync(
            name,
            new[] { embedding },
            thumbnail != null ? new[] { thumbnail } : null,
            notes: notes);
    }

    // ══════════════════════════════════════════════
    //  ENROLLMENT — Add more face samples
    // ══════════════════════════════════════════════

    /// <summary>
    /// Add an additional face sample to an existing patient.
    /// </summary>
    public async Task<Biometric> AddFaceSampleAsync(
        string pid,
        float[] embedding,
        byte[]? thumbnail = null,
        string? angle = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        _ = await db.Patients.FindAsync(pid)
            ?? throw new ArgumentException($"Patient with PID {pid} not found");

        var biometric = CreateFaceBiometric(embedding, thumbnail, angle);
        biometric.PID = pid;

        db.Biometrics.Add(biometric);
        await db.SaveChangesAsync();

        return biometric;
    }

    // ══════════════════════════════════════════════
    //  RECOGNITION LOGGING
    // ══════════════════════════════════════════════

    public async Task LogRecognitionAsync(
        string? pid,
        float distance,
        bool wasRecognized,
        bool passedLiveness,
        string? stationId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var log = new RecognitionLog
        {
            PID = pid,
            Distance = distance,
            WasRecognized = wasRecognized,
            PassedLiveness = passedLiveness,
            StationId = stationId,
            Timestamp = DateTime.UtcNow
        };

        db.RecognitionLogs.Add(log);
        await db.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════
    //  PERSON MANAGEMENT (CRUD)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Get all patients with their face sample counts.
    /// </summary>
    public async Task<List<PatientSummary>> GetAllPatientsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Patients
            .Select(ToSummary)
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Get a patient with all their biometrics.
    /// </summary>
    public async Task<Patient?> GetPatientWithBiometricsAsync(string pid)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Patients
            .Include(p => p.Biometrics)
            .FirstOrDefaultAsync(p => p.IDCard == pid);
    }

    /// <summary>
    /// Hard-delete a patient and all their related records.
    /// Cascade delete handles Biometrics and Visits.
    /// </summary>
    public async Task DeletePatientAsync(string pid)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var patient = await db.Patients.FindAsync(pid);
        if (patient != null)
        {
            db.Patients.Remove(patient);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Delete a specific biometric sample.
    /// </summary>
    public async Task DeleteBiometricAsync(int biometricId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var biometric = await db.Biometrics.FindAsync(biometricId);
        if (biometric != null)
        {
            db.Biometrics.Remove(biometric);
            await db.SaveChangesAsync();
        }
    }

    // ══════════════════════════════════════════════
    //  PATIENT SEARCH
    // ══════════════════════════════════════════════

    /// <summary>
    /// Search patients by name (LIKE search on FullName, MotherName, FatherName).
    /// </summary>
    public async Task<List<PatientSummary>> SearchPatientsByNameAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<PatientSummary>();

        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Patients
            .Where(p =>
                (p.FullName != null && p.FullName.Contains(query)) ||
                (p.MotherName != null && p.MotherName.Contains(query)) ||
                (p.FatherName != null && p.FatherName.Contains(query)) ||
                p.IDCard.Contains(query))
            .Select(ToSummary)
            .OrderBy(p => p.Name)
            .Take(50)
            .ToListAsync();
    }

    /// <summary>
    /// Look up a patient by their exact PID (IDCard).
    /// </summary>
    public async Task<Patient?> GetPatientByPidAsync(string pid)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Patients
            .Include(p => p.Biometrics)
            .Include(p => p.Visits)
            .FirstOrDefaultAsync(p => p.IDCard == pid);
    }

    /// <summary>
    /// Check for duplicate patients by name (deduplication before enrollment).
    /// </summary>
    public async Task<List<PatientSummary>> CheckDuplicateByNameAsync(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return new List<PatientSummary>();

        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Patients
            .Where(p => p.FullName == fullName)
            .Select(ToSummary)
            .ToListAsync();
    }

    /// <summary>
    /// Register a new patient with full demographics and face embedding.
    /// Used by the EnrolmentWindow multi-step wizard.
    /// </summary>
    public async Task<Patient> RegisterPatientAsync(
        Patient patient,
        float[] embedding,
        byte[]? thumbnail = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        patient.Biometrics.Add(CreateFaceBiometric(embedding, thumbnail));

        patient.CreatedOn ??= DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        patient.CreatedBy ??= Environment.UserName;

        db.Patients.Add(patient);
        await db.SaveChangesAsync();

        return patient;
    }

    /// <summary>
    /// Register a patient without any biometrics (e.g. when face capture was skipped with a remark).
    /// </summary>
    public async Task<Patient> RegisterPatientAsync(Patient patient)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        patient.CreatedOn ??= DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        patient.CreatedBy ??= Environment.UserName;

        db.Patients.Add(patient);
        await db.SaveChangesAsync();
        return patient;
    }

    /// <summary>
    /// Update a patient's demographics.
    /// </summary>
    public async Task UpdatePatientAsync(Patient patient)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        patient.LastModified = DateTime.UtcNow;
        patient.ModifiedBy = Environment.UserName;
        patient.ModifiedOn = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        db.Patients.Update(patient);
        await db.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════
    //  VISITS
    // ══════════════════════════════════════════════

    public async Task<Visit> CreateVisitAsync(Visit visit)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        db.Visits.Add(visit);
        await db.SaveChangesAsync();
        return visit;
    }

    public async Task<List<Visit>> GetPatientVisitsAsync(string pid)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Visits
            .Where(v => v.PID == pid)
            .OrderByDescending(v => v.Date)
            .ToListAsync();
    }

    // ══════════════════════════════════════════════
    //  FINGERPRINT TEMPLATES (via Biometrics table)
    // ══════════════════════════════════════════════

    /// <summary>
    /// Load all fingerprint templates for 1:N matching.
    /// Returns a dictionary mapping Biometric.Id → (template bytes, PID).
    /// </summary>
    public async Task<Dictionary<int, (byte[] Template, string PID)>> GetAllFingerprintTemplatesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var fingerprints = await db.Biometrics
            .Where(b => b.BiometricType != BiometricRemarks.Types.Face && b.Template != null)
            .Select(b => new { b.Id, b.Template, b.PID })
            .ToListAsync();

        return fingerprints.ToDictionary(
            f => f.Id,
            f => (f.Template!, f.PID));
    }

    /// <summary>
    /// Store a fingerprint enrollment template for a patient.
    /// Template can be null when capture failed (remark explains why).
    /// </summary>
    public async Task<Biometric> AddFingerprintTemplateAsync(
        string pid, string fingerType, byte[]? template, bool consent,
        string? remark = null)
    {
        var record = new Biometric
        {
            PID = pid,
            BiometricType = fingerType,
            Template = template,
            Consent = consent,
            Remark = remark,
            Date = DateTime.UtcNow,
            CreatedBy = Environment.UserName,
            CreatedDate = DateTime.UtcNow,
        };

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Biometrics.Add(record);
        await db.SaveChangesAsync();
        return record;
    }

    /// <summary>
    /// Get the patient who owns a specific biometric record (fingerprint or face).
    /// </summary>
    public async Task<Patient?> GetPatientByBiometricIdAsync(int biometricId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var biometric = await db.Biometrics
            .Include(b => b.Patient)
            .FirstOrDefaultAsync(b => b.Id == biometricId);
        return biometric?.Patient;
    }

    // ══════════════════════════════════════════════
    //  STATISTICS
    // ══════════════════════════════════════════════

    public async Task<DatabaseStats> GetStatsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Two queries instead of five: patients+embeddings, and recognition log aggregates
        var totalPatients = await db.Patients.CountAsync();
        var totalEmbeddings = await db.Biometrics.CountAsync(b => b.BiometricType == BiometricRemarks.Types.Face);

        var logCounts = await db.RecognitionLogs
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Recognized = g.Count(r => r.WasRecognized),
                LivenessFail = g.Count(r => !r.PassedLiveness)
            })
            .FirstOrDefaultAsync();

        var stats = new DatabaseStats
        {
            TotalPatients = totalPatients,
            TotalEmbeddings = totalEmbeddings,
            TotalRecognitions = logCounts?.Total ?? 0,
            SuccessfulRecognitions = logCounts?.Recognized ?? 0,
            LivenessFailures = logCounts?.LivenessFail ?? 0,
        };

        if (stats.TotalPatients > 0)
            stats.AverageSamplesPerPatient = (float)stats.TotalEmbeddings / stats.TotalPatients;

        if (stats.TotalRecognitions > 0)
            stats.RecognitionRate = (float)stats.SuccessfulRecognitions / stats.TotalRecognitions * 100f;

        return stats;
    }
}

// ══════════════════════════════════════════════
//  DTOs (Data Transfer Objects)
// ══════════════════════════════════════════════

public class FaceMatchResult
{
    public Patient Patient { get; set; } = null!;
    public int BiometricId { get; set; }
    public float Distance { get; set; }
    public bool IsMatch { get; set; }
    public float Similarity => 1f - Distance;
    public string SimilarityText => $"{Similarity * 100:F1}%";

    public bool IsHighConfidence =>
        IsMatch && Distance <= RecognitionSettings.HighConfidenceDistance;
}

public class PatientSummary
{
    public string IDCard { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Site { get; set; }
    public byte? Sex { get; set; }
    public string? Note { get; set; }
    public int FaceSampleCount { get; set; }
    public string? CreatedOn { get; set; }
}

public class DatabaseStats
{
    public int TotalPatients { get; set; }
    public int TotalEmbeddings { get; set; }
    public int TotalRecognitions { get; set; }
    public int SuccessfulRecognitions { get; set; }
    public int LivenessFailures { get; set; }
    public float AverageSamplesPerPatient { get; set; }
    public float RecognitionRate { get; set; }
}

public class VectorSearchRow
{
    public int Id { get; set; }
    public string PID { get; set; } = string.Empty;
    public double Distance { get; set; }
}
