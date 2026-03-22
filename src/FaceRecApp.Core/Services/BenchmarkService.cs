using System.Diagnostics;
using FaceRecApp.Core.Data;
using FaceRecApp.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FaceRecApp.Core.Services;

/// <summary>
/// Performance benchmarking service.
/// </summary>
public class BenchmarkService
{
    private readonly IDbContextFactory<FaceDbContext> _dbFactory;
    private readonly FaceRepository _repository;

    public BenchmarkService(
        IDbContextFactory<FaceDbContext> dbFactory,
        FaceRepository repository)
    {
        _dbFactory = dbFactory;
        _repository = repository;
    }

    public async Task<BenchmarkReport> RunFullBenchmarkAsync(int iterations = 10)
    {
        var report = new BenchmarkReport();

        await using var db = await _dbFactory.CreateDbContextAsync();
        report.TotalPatients = await db.Patients.CountAsync();
        report.TotalEmbeddings = await db.Biometrics.CountAsync(b => b.BiometricType == BiometricRemarks.Types.Face);

        if (report.TotalEmbeddings == 0)
        {
            report.Notes = "No embeddings in database. Register some faces first, then re-run benchmarks.";
            return report;
        }

        report.VectorSearchResults = await BenchmarkVectorSearchAsync(iterations);
        report.StatsQueryResults = await BenchmarkStatsQueryAsync(iterations);
        report.InsertResults = await BenchmarkInsertAsync(5);

        report.Timestamp = DateTime.UtcNow;
        return report;
    }

    public async Task<BenchmarkResult> BenchmarkVectorSearchAsync(int iterations = 10)
    {
        var result = new BenchmarkResult { Operation = "Vector Search (VECTOR_DISTANCE)" };

        var queryVector = GenerateRandomVector(RecognitionSettings.EmbeddingDimensions);
        await _repository.FindClosestMatchAsync(queryVector);

        var timings = new List<double>();
        for (int i = 0; i < iterations; i++)
        {
            queryVector = GenerateRandomVector(RecognitionSettings.EmbeddingDimensions);

            var sw = Stopwatch.StartNew();
            await _repository.FindClosestMatchAsync(queryVector);
            sw.Stop();

            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        result.Iterations = iterations;
        result.MinMs = timings.Min();
        result.MaxMs = timings.Max();
        result.AvgMs = timings.Average();
        result.MedianMs = GetMedian(timings);
        result.P95Ms = GetPercentile(timings, 95);

        return result;
    }

    public async Task<BenchmarkResult> BenchmarkStatsQueryAsync(int iterations = 10)
    {
        var result = new BenchmarkResult { Operation = "Stats Query (COUNT + AVG)" };

        await _repository.GetStatsAsync();

        var timings = new List<double>();
        for (int i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            await _repository.GetStatsAsync();
            sw.Stop();
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        result.Iterations = iterations;
        result.MinMs = timings.Min();
        result.MaxMs = timings.Max();
        result.AvgMs = timings.Average();
        result.MedianMs = GetMedian(timings);
        result.P95Ms = GetPercentile(timings, 95);

        return result;
    }

    public async Task<BenchmarkResult> BenchmarkInsertAsync(int iterations = 5)
    {
        var result = new BenchmarkResult { Operation = "Insert Embedding" };

        var testPatient = await _repository.RegisterPatientAsync(
            $"__benchmark_test_{Guid.NewGuid():N}",
            GenerateRandomVector(RecognitionSettings.EmbeddingDimensions),
            notes: "Benchmark test -- safe to delete");

        var timings = new List<double>();
        for (int i = 0; i < iterations; i++)
        {
            var embedding = GenerateRandomVector(RecognitionSettings.EmbeddingDimensions);

            var sw = Stopwatch.StartNew();
            await _repository.AddFaceSampleAsync(testPatient.IDCard, embedding, angle: $"bench_{i}");
            sw.Stop();

            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        await _repository.DeletePatientAsync(testPatient.IDCard);

        result.Iterations = iterations;
        result.MinMs = timings.Min();
        result.MaxMs = timings.Max();
        result.AvgMs = timings.Average();
        result.MedianMs = GetMedian(timings);
        result.P95Ms = GetPercentile(timings, 95);

        return result;
    }

    /// <summary>
    /// Populate the database with synthetic face embeddings for scale testing.
    /// </summary>
    public async Task<int> PopulateSyntheticDataAsync(
        int personCount,
        int samplesPerPerson = 1,
        Action<int, int>? progress = null)
    {
        const int batchSize = 500;
        int totalInserted = 0;
        var sw = Stopwatch.StartNew();

        for (int batchStart = 0; batchStart < personCount; batchStart += batchSize)
        {
            int batchEnd = Math.Min(batchStart + batchSize, personCount);

            await using var db = await _dbFactory.CreateDbContextAsync();

            for (int i = batchStart; i < batchEnd; i++)
            {
                var patient = new Patient
                {
                    FullName = $"Synthetic Person #{i + 1:D6}",
                    IDCard = $"X{i + 1:D5}",
                    Site = "X",
                    Note = "Synthetic benchmark data -- safe to delete",
                    CreatedOn = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                };

                for (int j = 0; j < samplesPerPerson; j++)
                {
                    patient.Biometrics.Add(new Biometric
                    {
                        BiometricType = BiometricRemarks.Types.Face,
                        Embedding = GenerateRandomVector(RecognitionSettings.EmbeddingDimensions),
                        CaptureAngle = "synthetic",
                        Date = DateTime.UtcNow,
                        Consent = true,
                    });
                }

                db.Patients.Add(patient);
            }

            await db.SaveChangesAsync();
            totalInserted += (batchEnd - batchStart) * samplesPerPerson;

            progress?.Invoke(batchEnd, personCount);
            Console.WriteLine($"[Benchmark] Populated {batchEnd:N0}/{personCount:N0} persons " +
                              $"({totalInserted:N0} embeddings, {sw.Elapsed.TotalSeconds:F1}s)");
        }

        sw.Stop();
        Console.WriteLine($"[Benchmark] Done: {personCount:N0} persons, {totalInserted:N0} embeddings in {sw.Elapsed.TotalSeconds:F1}s");
        return totalInserted;
    }

    public async Task CleanupSyntheticDataAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var synthetics = await db.Patients
            .Where(p => p.Note == "Synthetic benchmark data -- safe to delete"
                     || (p.Note != null && p.Note.StartsWith("Benchmark test")))
            .ToListAsync();

        db.Patients.RemoveRange(synthetics);
        await db.SaveChangesAsync();
    }

    // ── Helpers ──

    private static float[] GenerateRandomVector(int dimensions)
    {
        var rng = Random.Shared;
        var vector = new float[dimensions];
        for (int i = 0; i < dimensions; i++)
            vector[i] = (float)(rng.NextDouble() * 2 - 1);

        float norm = MathF.Sqrt(vector.Sum(v => v * v));
        for (int i = 0; i < dimensions; i++)
            vector[i] /= norm;

        return vector;
    }

    private static double GetMedian(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    private static double GetPercentile(List<double> values, int percentile)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Max(0, index)];
    }
}

// ── Report DTOs ──

public class BenchmarkReport
{
    public DateTime Timestamp { get; set; }
    public int TotalPatients { get; set; }
    public int TotalEmbeddings { get; set; }
    public string? Notes { get; set; }

    public BenchmarkResult? VectorSearchResults { get; set; }
    public BenchmarkResult? StatsQueryResults { get; set; }
    public BenchmarkResult? InsertResults { get; set; }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("===========================================");
        sb.AppendLine("  FACE RECOGNITION - PERFORMANCE REPORT");
        sb.AppendLine("===========================================");
        sb.AppendLine($"  Date:       {Timestamp:yyyy-MM-dd HH:mm:ss UTC}");
        sb.AppendLine($"  Persons:    {TotalPatients:N0}");
        sb.AppendLine($"  Embeddings: {TotalEmbeddings:N0}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(Notes))
        {
            sb.AppendLine($"  Note: {Notes}");
            sb.AppendLine();
        }

        if (VectorSearchResults != null) sb.AppendLine(VectorSearchResults.ToString());
        if (StatsQueryResults != null) sb.AppendLine(StatsQueryResults.ToString());
        if (InsertResults != null) sb.AppendLine(InsertResults.ToString());

        sb.AppendLine("===========================================");
        return sb.ToString();
    }
}

public class BenchmarkResult
{
    public string Operation { get; set; } = "";
    public int Iterations { get; set; }
    public double MinMs { get; set; }
    public double MaxMs { get; set; }
    public double AvgMs { get; set; }
    public double MedianMs { get; set; }
    public double P95Ms { get; set; }

    public override string ToString()
    {
        return $"  -- {Operation} ({Iterations} iterations) --\n" +
               $"    Min:    {MinMs,8:F2} ms\n" +
               $"    Max:    {MaxMs,8:F2} ms\n" +
               $"    Avg:    {AvgMs,8:F2} ms\n" +
               $"    Median: {MedianMs,8:F2} ms\n" +
               $"    P95:    {P95Ms,8:F2} ms\n";
    }
}
