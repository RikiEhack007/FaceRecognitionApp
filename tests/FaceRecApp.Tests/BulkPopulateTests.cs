using FaceRecApp.Core.Data;
using FaceRecApp.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FaceRecApp.Tests;

/// <summary>
/// Bulk data population for vector search scale testing.
/// Connects to the real SQL Server 2025 database.
///
/// Run with:
///   dotnet test tests/FaceRecApp.Tests --filter "FullyQualifiedName~BulkPopulateTests" -v n
/// </summary>
public class BulkPopulateTests
{
    private const string CONNECTION_STRING =
        "Server=localhost,60240;Database=FaceRecognitionDb;" +
        "Trusted_Connection=true;TrustServerCertificate=true;" +
        "MultipleActiveResultSets=true;";

    private readonly ITestOutputHelper _output;

    public BulkPopulateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private IDbContextFactory<FaceDbContext> CreateFactory()
    {
        var options = new DbContextOptionsBuilder<FaceDbContext>()
            .UseSqlServer(CONNECTION_STRING, sql => sql.UseVectorSearch())
            .Options;
        return new TestDbContextFactory(options);
    }

    [Fact]
    public async Task Populate_100K_SyntheticPersons()
    {
        var dbFactory = CreateFactory();
        var repository = new FaceRepository(dbFactory);
        var benchmark = new BenchmarkService(dbFactory, repository);

        // Check current count
        await using var db = await dbFactory.CreateDbContextAsync();
        var existingPersons = await db.Persons.CountAsync();
        var existingEmbeddings = await db.FaceEmbeddings.CountAsync();
        _output.WriteLine($"Before: {existingPersons:N0} persons, {existingEmbeddings:N0} embeddings");

        // Populate 100,000 synthetic persons (1 embedding each = 100K embeddings)
        var total = await benchmark.PopulateSyntheticDataAsync(
            personCount: 100_000,
            samplesPerPerson: 1,
            progress: (done, total) =>
            {
                _output.WriteLine($"Progress: {done:N0}/{total:N0} ({done * 100.0 / total:F1}%)");
            });

        // Verify
        var finalPersons = await db.Persons.CountAsync();
        var finalEmbeddings = await db.FaceEmbeddings.CountAsync();
        _output.WriteLine($"After: {finalPersons:N0} persons, {finalEmbeddings:N0} embeddings");
        _output.WriteLine($"Inserted: {total:N0} embeddings");

        Assert.True(total > 0);
    }
}

/// <summary>
/// Simple IDbContextFactory wrapper for tests.
/// </summary>
public class TestDbContextFactory : IDbContextFactory<FaceDbContext>
{
    private readonly DbContextOptions<FaceDbContext> _options;

    public TestDbContextFactory(DbContextOptions<FaceDbContext> options)
    {
        _options = options;
    }

    public FaceDbContext CreateDbContext() => new(_options);
}
