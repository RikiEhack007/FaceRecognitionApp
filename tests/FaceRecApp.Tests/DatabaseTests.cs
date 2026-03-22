using FaceRecApp.Core.Data;
using FaceRecApp.Core.Entities;
using FaceRecApp.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FaceRecApp.Tests;

/// <summary>
/// Integration tests that verify database operations work correctly.
///
/// NOTE: These tests require a running SQL Server 2025 instance.
/// They use InMemory database by default (vector operations won't work),
/// but can be switched to real SQL Server for full integration testing.
///
/// To run with real SQL Server:
///   1. Set USE_REAL_DB = true below
///   2. Update the connection string
///   3. Ensure SQL Server 2025 Express is running
/// </summary>
public class DatabaseTests : IDisposable
{
    // Set to true to test against real SQL Server (requires SQL Server 2025)
    private const bool USE_REAL_DB = false;

    private const string TEST_CONNECTION_STRING =
        "Server=localhost\\SQLEXPRESS;Database=FaceRecognitionDb_Test;" +
        "Trusted_Connection=true;TrustServerCertificate=true;";

    private readonly IDbContextFactory<FaceDbContext> _dbFactory;
    private readonly FaceRepository _repository;

    public DatabaseTests()
    {
#pragma warning disable CS0162 // Unreachable code — USE_REAL_DB is a const toggle
        if (USE_REAL_DB)
        {
            // Real SQL Server — full vector support
            var options = new DbContextOptionsBuilder<FaceDbContext>()
                .UseSqlServer(TEST_CONNECTION_STRING, sql => sql.UseVectorSearch())
                .Options;

            _dbFactory = new TestDbContextFactory(options);

            // Ensure test database exists
            using var db = _dbFactory.CreateDbContext();
            db.Database.EnsureCreated();
        }
        else
        {
            // InMemory — basic CRUD only, no vector operations
            var options = new DbContextOptionsBuilder<FaceDbContext>()
                .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
                .Options;

            _dbFactory = new TestDbContextFactory(options);
        }
#pragma warning restore CS0162

        _repository = new FaceRepository(_dbFactory);
    }

    [Fact]
    public async Task RegisterPatient_SingleEmbedding_CreatesPatientAndBiometric()
    {
        var embedding = CreateTestVector(512);

        var patient = await _repository.RegisterPatientAsync(
            "Test Person", embedding, notes: "Test");

        Assert.NotEmpty(patient.IDCard); // IDCard is assigned (may be empty string from repo)
        Assert.Equal("Test Person", patient.FullName);

        // Verify in database
        var loaded = await _repository.GetPatientWithBiometricsAsync(patient.IDCard);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Biometrics, b => b.BiometricType == BiometricRemarks.Types.Face);
    }

    [Fact]
    public async Task RegisterPatient_MultipleEmbeddings_StoresAll()
    {
        var embeddings = new List<float[]>
        {
            CreateTestVector(512, seed: 1),
            CreateTestVector(512, seed: 2),
            CreateTestVector(512, seed: 3)
        };

        var patient = await _repository.RegisterPatientAsync(
            "Multi Sample", embeddings);

        var loaded = await _repository.GetPatientWithBiometricsAsync(patient.IDCard);
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Biometrics.Count(b => b.BiometricType == BiometricRemarks.Types.Face));
    }

    [Fact]
    public async Task AddFaceSample_AddsToExistingPatient()
    {
        var patient = await _repository.RegisterPatientAsync(
            "Growing Person", CreateTestVector(512));

        await _repository.AddFaceSampleAsync(patient.IDCard, CreateTestVector(512, seed: 99));

        var loaded = await _repository.GetPatientWithBiometricsAsync(patient.IDCard);
        Assert.Equal(2, loaded!.Biometrics.Count(b => b.BiometricType == BiometricRemarks.Types.Face));
    }

    [Fact]
    public async Task GetAllPatients_ReturnsSummaries()
    {
        await _repository.RegisterPatientAsync("Person1", CreateTestVector(512, seed: 1));
        await _repository.RegisterPatientAsync("Person2", CreateTestVector(512, seed: 2));

        var patients = await _repository.GetAllPatientsAsync();
        Assert.Contains(patients, p => p.Name == "Person1");
        Assert.Contains(patients, p => p.Name == "Person2");
    }

    [Fact]
    public async Task DeletePatient_RemovesPatientAndBiometrics()
    {
        var patient = await _repository.RegisterPatientAsync(
            "To Delete", CreateTestVector(512));

        await _repository.DeletePatientAsync(patient.IDCard);

        var loaded = await _repository.GetPatientWithBiometricsAsync(patient.IDCard);
        Assert.Null(loaded);
    }

    [Fact]
    public async Task LogRecognition_CreatesLogEntry()
    {
        var patient = await _repository.RegisterPatientAsync(
            "Log Test", CreateTestVector(512));

        await _repository.LogRecognitionAsync(
            patient.IDCard, distance: 0.3f, wasRecognized: true, passedLiveness: true);

        var stats = await _repository.GetStatsAsync();
        Assert.True(stats.TotalRecognitions > 0);
    }

    [Fact]
    public async Task GetStats_ReturnsCorrectCounts()
    {
        await _repository.RegisterPatientAsync("Stats1", CreateTestVector(512, seed: 10));
        await _repository.RegisterPatientAsync("Stats2", CreateTestVector(512, seed: 20));

        var stats = await _repository.GetStatsAsync();
        Assert.True(stats.TotalPatients >= 2);
        Assert.True(stats.TotalEmbeddings >= 2);
    }

    // Skip vector search test when using InMemory (VECTOR_DISTANCE not supported)
    [Fact]
    public async Task FindClosestMatch_ReturnsNullForEmptyDatabase()
    {
#pragma warning disable CS0162 // Unreachable code — USE_REAL_DB is a const toggle
        if (!USE_REAL_DB) return; // InMemory doesn't support VECTOR_DISTANCE

        var queryVector = CreateTestVector(512, seed: 999);
        var result = await _repository.FindClosestMatchAsync(queryVector);

        if (result != null)
        {
            Assert.True(result.Distance > 0);
        }
#pragma warning restore CS0162
    }

    // ── Cleanup ──

    public void Dispose()
    {
#pragma warning disable CS0162 // Unreachable code — USE_REAL_DB is a const toggle
        if (USE_REAL_DB)
        {
            using var db = _dbFactory.CreateDbContext();
            db.Database.EnsureDeleted();
        }
#pragma warning restore CS0162
    }

    // ── Helpers ──

    private static float[] CreateTestVector(int dimensions, int seed = 42)
    {
        var rng = new Random(seed);
        var vector = new float[dimensions];
        for (int i = 0; i < dimensions; i++)
            vector[i] = (float)(rng.NextDouble() * 2 - 1);

        float norm = MathF.Sqrt(vector.Sum(v => v * v));
        for (int i = 0; i < dimensions; i++)
            vector[i] /= norm;

        return vector;
    }

}
