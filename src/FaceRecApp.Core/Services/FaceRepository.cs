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
///   - RegisterPatient(): Stores a new patient with face embeddings.
///
/// Thread safety:
///   Uses IDbContextFactory to create short-lived DbContext instances per operation.
/// </summary>
public class FaceRepository
{
    private readonly IDbContextFactory<FaceDbContext> _dbFactory;
    private static volatile bool _useVectorSearch;

    private static readonly System.Linq.Expressions.Expression<Func<Patient, PatientSummary>> ToSummary =
        p => new PatientSummary
        {
            IDCard = p.IDCard,
            Name = p.FullName ?? "",
            Site = p.Site,
            Sex = p.Sex,
            Note = p.Note,
            FaceSampleCount = p.FaceEmbeddings.Count,
            CreatedOn = p.CreatedOn,
        };

    public FaceRepository(IDbContextFactory<FaceDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

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

    public bool UseVectorSearch => _useVectorSearch;

    // ══════════════════════════════════════════════
    //  VECTOR SEARCH — The core matching operation
    // ══════════════════════════════════════════════

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

        var match = await db.FaceEmbeddings
            .Include(e => e.Patient)
            .Where(e => e.Embedding != null)
            .Select(e => new
            {
                FaceEmbedding = e,
                Distance = EF.Functions.VectorDistance("cosine", e.Embedding!, queryEmbedding)
            })
            .OrderBy(x => x.Distance)
            .FirstOrDefaultAsync();

        if (match == null)
            return null;

        return new FaceMatchResult
        {
            Patient = match.FaceEmbedding.Patient,
            BiometricId = match.FaceEmbedding.Id,
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

    public async Task<List<FaceMatchResult>> FindTopMatchesAsync(float[] queryEmbedding, int topN = 5)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var matches = await db.FaceEmbeddings
            .Include(e => e.Patient)
            .Where(e => e.Embedding != null)
            .Select(e => new
            {
                FaceEmbedding = e,
                Distance = EF.Functions.VectorDistance("cosine", e.Embedding!, queryEmbedding)
            })
            .OrderBy(x => x.Distance)
            .Take(topN)
            .ToListAsync();

        return matches.Select(m => new FaceMatchResult
        {
            Patient = m.FaceEmbedding.Patient,
            BiometricId = m.FaceEmbedding.Id,
            Distance = (float)m.Distance,
            IsMatch = m.Distance <= RecognitionSettings.DistanceThreshold
        }).ToList();
    }

    // ══════════════════════════════════════════════
    //  VERIFY — 1:1 comparison against a specific patient
    // ══════════════════════════════════════════════

    public async Task<FaceMatchResult?> VerifyAgainstPatientAsync(string pid, float[] queryEmbedding)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var match = await db.FaceEmbeddings
            .Include(e => e.Patient)
            .Where(e => e.PID == pid && e.Embedding != null)
            .Select(e => new
            {
                FaceEmbedding = e,
                Distance = EF.Functions.VectorDistance("cosine", e.Embedding!, queryEmbedding)
            })
            .OrderBy(x => x.Distance)
            .FirstOrDefaultAsync();

        if (match == null)
            return null;

        return new FaceMatchResult
        {
            Patient = match.FaceEmbedding.Patient,
            BiometricId = match.FaceEmbedding.Id,
            Distance = (float)match.Distance,
            IsMatch = match.Distance <= RecognitionSettings.DistanceThreshold
        };
    }

    private static FaceEmbedding CreateFaceEmbedding(float[] embedding, byte[]? thumbnail, string? angle = "front")
        => new()
        {
            Embedding = embedding,
            FaceThumbnail = thumbnail,
            CaptureAngle = angle,
            CapturedAt = DateTime.UtcNow,
            Consent = true,
            CreatedBy = Environment.UserName,
            CreatedDate = DateTime.UtcNow,
        };

    // ══════════════════════════════════════════════
    //  REGISTRATION
    // ══════════════════════════════════════════════

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
            patient.FaceEmbeddings.Add(CreateFaceEmbedding(embeddings[i], thumb, angle));
        }

        db.Patients.Add(patient);
        await db.SaveChangesAsync();

        return patient;
    }

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

    public async Task<FaceEmbedding> AddFaceSampleAsync(
        string pid,
        float[] embedding,
        byte[]? thumbnail = null,
        string? angle = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        _ = await db.Patients.FindAsync(pid)
            ?? throw new ArgumentException($"Patient with PID {pid} not found");

        var faceEmbedding = CreateFaceEmbedding(embedding, thumbnail, angle);
        faceEmbedding.PID = pid;

        db.FaceEmbeddings.Add(faceEmbedding);
        await db.SaveChangesAsync();

        return faceEmbedding;
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
    //  PATIENT MANAGEMENT (CRUD)
    // ══════════════════════════════════════════════

    public async Task<List<PatientSummary>> GetAllPatientsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Patients
            .Select(ToSummary)
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    public async Task<Patient?> GetPatientWithBiometricsAsync(string pid)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Patients
            .Include(p => p.FaceEmbeddings)
            .Include(p => p.FingerprintTemplates)
            .FirstOrDefaultAsync(p => p.IDCard == pid);
    }

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

    public async Task DeleteFaceEmbeddingAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var face = await db.FaceEmbeddings.FindAsync(id);
        if (face != null)
        {
            db.FaceEmbeddings.Remove(face);
            await db.SaveChangesAsync();
        }
    }

    public async Task DeleteFingerprintTemplateAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var fp = await db.FingerprintTemplates.FindAsync(id);
        if (fp != null)
        {
            db.FingerprintTemplates.Remove(fp);
            await db.SaveChangesAsync();
        }
    }

    // ══════════════════════════════════════════════
    //  PATIENT SEARCH
    // ══════════════════════════════════════════════

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

    public async Task<Patient?> GetPatientByPidAsync(string pid)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Patients
            .Include(p => p.FaceEmbeddings)
            .Include(p => p.FingerprintTemplates)
            .Include(p => p.Visits)
            .FirstOrDefaultAsync(p => p.IDCard == pid);
    }

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

    public async Task<Patient> RegisterPatientAsync(
        Patient patient,
        float[] embedding,
        byte[]? thumbnail = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        patient.FaceEmbeddings.Add(CreateFaceEmbedding(embedding, thumbnail));

        patient.CreatedOn ??= DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        patient.CreatedBy ??= Environment.UserName;

        db.Patients.Add(patient);
        await db.SaveChangesAsync();

        return patient;
    }

    public async Task<Patient> RegisterPatientAsync(Patient patient)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        patient.CreatedOn ??= DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        patient.CreatedBy ??= Environment.UserName;

        db.Patients.Add(patient);
        await db.SaveChangesAsync();
        return patient;
    }

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
    //  FINGERPRINT TEMPLATES
    // ══════════════════════════════════════════════

    public async Task<Dictionary<int, (byte[] Template, string PID)>> GetAllFingerprintTemplatesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var fingerprints = await db.FingerprintTemplates
            .Where(t => t.Template != null)
            .Select(t => new { t.Id, t.Template, t.PID })
            .ToListAsync();

        return fingerprints.ToDictionary(
            f => f.Id,
            f => (f.Template!, f.PID));
    }

    public async Task<FingerprintTemplate> AddFingerprintTemplateAsync(
        string pid, string fingerType, byte[]? template, bool consent,
        string? remark = null)
    {
        var record = new FingerprintTemplate
        {
            PID = pid,
            FingerType = fingerType,
            Template = template,
            Consent = consent,
            Remark = remark,
            CaptureDate = DateTime.UtcNow,
            CreatedBy = Environment.UserName,
            CreatedDate = DateTime.UtcNow,
        };

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.FingerprintTemplates.Add(record);
        await db.SaveChangesAsync();
        return record;
    }

    public async Task<Patient?> GetPatientByFingerprintIdAsync(int fingerprintId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var fp = await db.FingerprintTemplates
            .Include(t => t.Patient)
            .FirstOrDefaultAsync(t => t.Id == fingerprintId);
        return fp?.Patient;
    }

    // ══════════════════════════════════════════════
    //  STATISTICS
    // ══════════════════════════════════════════════

    public async Task<DatabaseStats> GetStatsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var totalPatients = await db.Patients.CountAsync();
        var totalEmbeddings = await db.FaceEmbeddings.CountAsync();

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
