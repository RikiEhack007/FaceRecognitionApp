using System.Diagnostics;
using FaceRecApp.Core.Data;
using FaceRecApp.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FaceRecApp.Core.Services;

/// <summary>
/// Performance benchmarking service.
/// 
/// Measures key operations:
///   - Face detection speed (SCRFD model)
///   - Embedding generation speed (ArcFace model)
///   - SQL vector search speed (VECTOR_DISTANCE)
///   - End-to-end pipeline speed
/// 
/// Use this to:
///   - Verify the system meets speed requirements
///   - Compare exact vs approximate (DiskANN) search
///   - Identify bottlenecks
///   - Generate performance reports for stakeholders
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

    /// <summary>
    /// Run the full benchmark suite and return results.
    /// </summary>
    public async Task<BenchmarkReport> RunFullBenchmarkAsync(int iterations = 10)
    {
        var report = new BenchmarkReport();

        // Get database stats
        await using var db = await _dbFactory.CreateDbContextAsync();
        report.TotalPersons = await db.Persons.CountAsync(p => p.IsActive);
        report.TotalEmbeddings = await db.FaceEmbeddings.CountAsync();

        if (report.TotalEmbeddings == 0)
        {
            report.Notes = "No embeddings in database. Register some faces first, then re-run benchmarks.";
            return report;
        }

        // ── Vector Search Benchmark ──
        report.VectorSearchResults = await BenchmarkVectorSearchAsync(iterations);

        // ── Database Stats Query Benchmark ──
        report.StatsQueryResults = await BenchmarkStatsQueryAsync(iterations);

        // ── Insert Benchmark ──
        report.InsertResults = await BenchmarkInsertAsync(5);

        report.Timestamp = DateTime.UtcNow;
        return report;
    }

    /// <summary>
    /// Benchmark SQL Server VECTOR_DISTANCE search.
    /// This is the most critical operation — it runs on every processed frame.
    /// </summary>
    public async Task<BenchmarkResult> BenchmarkVectorSearchAsync(int iterations = 10)
    {
        var result = new BenchmarkResult { Operation = "Vector Search (VECTOR_DISTANCE)" };

        // Generate a random query vector (simulates a new face)
        var queryVector = GenerateRandomVector(RecognitionSettings.EmbeddingDimensions);

        // Warm-up run (first query is always slower due to plan compilation)
        await _repository.FindClosestMatchAsync(queryVector);

        // Timed runs
        var timings = new List<double>();
        for (int i = 0; i < iterations; i++)
        {
            // Use a slightly different vector each time to avoid caching
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

    /// <summary>
    /// Benchmark the stats query (used for dashboard).
    /// </summary>
    public async Task<BenchmarkResult> BenchmarkStatsQueryAsync(int iterations = 10)
    {
        var result = new BenchmarkResult { Operation = "Stats Query (COUNT + AVG)" };

        // Warm-up
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

    /// <summary>
    /// Benchmark inserting new embeddings.
    /// </summary>
    public async Task<BenchmarkResult> BenchmarkInsertAsync(int iterations = 5)
    {
        var result = new BenchmarkResult { Operation = "Insert Embedding" };

        // Create a temporary person for testing
        var testPerson = await _repository.RegisterPersonAsync(
            $"__benchmark_test_{Guid.NewGuid():N}",
            GenerateRandomVector(RecognitionSettings.EmbeddingDimensions),
            notes: "Benchmark test — safe to delete");

        var timings = new List<double>();
        for (int i = 0; i < iterations; i++)
        {
            var embedding = GenerateRandomVector(RecognitionSettings.EmbeddingDimensions);

            var sw = Stopwatch.StartNew();
            await _repository.AddFaceSampleAsync(testPerson.Id, embedding, angle: $"bench_{i}");
            sw.Stop();

            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        // Clean up test data
        await _repository.DeletePersonAsync(testPerson.Id);

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
    /// Uses batch inserts for high performance (~500 persons per SaveChanges).
    ///
    /// WARNING: This adds fake data. Use only for benchmarking.
    /// </summary>
    /// <param name="personCount">Number of synthetic persons to create</param>
    /// <param name="samplesPerPerson">Embeddings per person (default 1 for scale tests)</param>
    /// <param name="progress">Optional callback: (inserted, total) for UI progress</param>
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
                var person = new Person
                {
                    Name = $"Synthetic Person #{i + 1:D6}",
                    Notes = "Synthetic benchmark data -- safe to delete",
                    CreatedAt = DateTime.UtcNow,
                    LastSeenAt = DateTime.UtcNow,
                    IsActive = true
                };

                for (int j = 0; j < samplesPerPerson; j++)
                {
                    person.FaceEmbeddings.Add(new FaceEmbedding
                    {
                        Embedding = GenerateRandomVector(RecognitionSettings.EmbeddingDimensions),
                        CaptureAngle = "synthetic",
                        CapturedAt = DateTime.UtcNow
                    });
                }

                db.Persons.Add(person);
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

    /// <summary>
    /// Remove all synthetic benchmark data.
    /// </summary>
    public async Task CleanupSyntheticDataAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var synthetics = await db.Persons
            .Where(p => p.Notes == "Synthetic benchmark data -- safe to delete"
                     || p.Notes == "Synthetic benchmark data — safe to delete"
                     || (p.Notes != null && p.Notes.StartsWith("Benchmark test")))
            .ToListAsync();

        db.Persons.RemoveRange(synthetics);
        await db.SaveChangesAsync();
    }

    // ── Helpers ──

    private static float[] GenerateRandomVector(int dimensions)
    {
        var rng = Random.Shared;
        var vector = new float[dimensions];
        for (int i = 0; i < dimensions; i++)
            vector[i] = (float)(rng.NextDouble() * 2 - 1);

        // L2 normalize
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
    public int TotalPersons { get; set; }
    public int TotalEmbeddings { get; set; }
    public string? Notes { get; set; }

    public BenchmarkResult? VectorSearchResults { get; set; }
    public BenchmarkResult? StatsQueryResults { get; set; }
    public BenchmarkResult? InsertResults { get; set; }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════");
        sb.AppendLine("  FACE RECOGNITION — PERFORMANCE REPORT");
        sb.AppendLine("═══════════════════════════════════════════");
        sb.AppendLine($"  Date:       {Timestamp:yyyy-MM-dd HH:mm:ss UTC}");
        sb.AppendLine($"  Persons:    {TotalPersons:N0}");
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

        sb.AppendLine("═══════════════════════════════════════════");
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
        return $"  ── {Operation} ({Iterations} iterations) ──\n" +
               $"    Min:    {MinMs,8:F2} ms\n" +
               $"    Max:    {MaxMs,8:F2} ms\n" +
               $"    Avg:    {AvgMs,8:F2} ms\n" +
               $"    Median: {MedianMs,8:F2} ms\n" +
               $"    P95:    {P95Ms,8:F2} ms\n";
    }
}
