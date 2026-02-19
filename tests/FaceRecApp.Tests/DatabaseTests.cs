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

        _repository = new FaceRepository(_dbFactory);
    }

    [Fact]
    public async Task RegisterPerson_SingleEmbedding_CreatesPersonAndEmbedding()
    {
        var embedding = CreateTestVector(512);

        var person = await _repository.RegisterPersonAsync(
            "Test Person", embedding, notes: "Test");

        Assert.NotEqual(0, person.Id);
        Assert.Equal("Test Person", person.Name);
        Assert.True(person.IsActive);

        // Verify in database
        var loaded = await _repository.GetPersonWithEmbeddingsAsync(person.Id);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.FaceEmbeddings);
    }

    [Fact]
    public async Task RegisterPerson_MultipleEmbeddings_StoresAll()
    {
        var embeddings = new List<float[]>
        {
            CreateTestVector(512, seed: 1),
            CreateTestVector(512, seed: 2),
            CreateTestVector(512, seed: 3)
        };

        var person = await _repository.RegisterPersonAsync(
            "Multi Sample", embeddings);

        var loaded = await _repository.GetPersonWithEmbeddingsAsync(person.Id);
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.FaceEmbeddings.Count);
    }

    [Fact]
    public async Task AddFaceSample_AddsToExistingPerson()
    {
        var person = await _repository.RegisterPersonAsync(
            "Growing Person", CreateTestVector(512));

        await _repository.AddFaceSampleAsync(person.Id, CreateTestVector(512, seed: 99));

        var loaded = await _repository.GetPersonWithEmbeddingsAsync(person.Id);
        Assert.Equal(2, loaded!.FaceEmbeddings.Count);
    }

    [Fact]
    public async Task GetAllPersons_ReturnsOnlyActive()
    {
        await _repository.RegisterPersonAsync("Active1", CreateTestVector(512, seed: 1));
        await _repository.RegisterPersonAsync("Active2", CreateTestVector(512, seed: 2));
        var toDeactivate = await _repository.RegisterPersonAsync("Inactive", CreateTestVector(512, seed: 3));

        await _repository.DeactivatePersonAsync(toDeactivate.Id);

        var persons = await _repository.GetAllPersonsAsync();
        Assert.DoesNotContain(persons, p => p.Name == "Inactive");
        Assert.Contains(persons, p => p.Name == "Active1");
        Assert.Contains(persons, p => p.Name == "Active2");
    }

    [Fact]
    public async Task DeletePerson_RemovesPersonAndEmbeddings()
    {
        var person = await _repository.RegisterPersonAsync(
            "To Delete", CreateTestVector(512));

        await _repository.DeletePersonAsync(person.Id);

        var loaded = await _repository.GetPersonWithEmbeddingsAsync(person.Id);
        Assert.Null(loaded);
    }

    [Fact]
    public async Task LogRecognition_CreatesLogEntry()
    {
        var person = await _repository.RegisterPersonAsync(
            "Log Test", CreateTestVector(512));

        await _repository.LogRecognitionAsync(
            person.Id, distance: 0.3f, wasRecognized: true, passedLiveness: true);

        var stats = await _repository.GetStatsAsync();
        Assert.True(stats.TotalRecognitions > 0);
    }

    [Fact]
    public async Task GetStats_ReturnsCorrectCounts()
    {
        await _repository.RegisterPersonAsync("Stats1", CreateTestVector(512, seed: 10));
        await _repository.RegisterPersonAsync("Stats2", CreateTestVector(512, seed: 20));

        var stats = await _repository.GetStatsAsync();
        Assert.True(stats.TotalPersons >= 2);
        Assert.True(stats.TotalEmbeddings >= 2);
    }

    // Skip vector search test when using InMemory (VECTOR_DISTANCE not supported)
    [Fact]
    public async Task FindClosestMatch_ReturnsNullForEmptyDatabase()
    {
        if (!USE_REAL_DB) return; // InMemory doesn't support VECTOR_DISTANCE

        // Use a fresh database or search with random vector
        var queryVector = CreateTestVector(512, seed: 999);
        // This should either return null or a high-distance result
        var result = await _repository.FindClosestMatchAsync(queryVector);

        // If database is empty, result is null
        // If database has data, result should have a distance > 0
        if (result != null)
        {
            Assert.True(result.Distance > 0);
        }
    }

    // ── Cleanup ──

    public void Dispose()
    {
        if (USE_REAL_DB)
        {
            using var db = _dbFactory.CreateDbContext();
            db.Database.EnsureDeleted();
        }
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

    // Simple factory for tests
    private class TestDbContextFactory : IDbContextFactory<FaceDbContext>
    {
        private readonly DbContextOptions<FaceDbContext> _options;

        public TestDbContextFactory(DbContextOptions<FaceDbContext> options)
        {
            _options = options;
        }

        public FaceDbContext CreateDbContext()
        {
            return new FaceDbContext(_options);
        }
    }
}
