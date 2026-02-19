using FaceRecApp.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FaceRecApp.Core.Data;

/// <summary>
/// Entity Framework Core database context for the face recognition system.
/// 
/// Targets SQL Server 2025 with native VECTOR(512) column type for face embeddings.
/// 
/// Setup:
///   1. Install SQL Server 2025 Express
///   2. Update connection string in appsettings.json
///   3. Run: dotnet ef migrations add InitialCreate -p src/FaceRecApp.Core -s src/FaceRecApp.WPF
///   4. Run: dotnet ef database update -p src/FaceRecApp.Core -s src/FaceRecApp.WPF
///   
/// The database will be created automatically with:
///   - Persons table (registered individuals)
///   - FaceEmbeddings table (with VECTOR(512) column)
///   - RecognitionLogs table (audit trail)
/// </summary>
public class FaceDbContext : DbContext
{
    public DbSet<Person> Persons => Set<Person>();
    public DbSet<FaceEmbedding> FaceEmbeddings => Set<FaceEmbedding>();
    public DbSet<RecognitionLog> RecognitionLogs => Set<RecognitionLog>();

    public FaceDbContext(DbContextOptions<FaceDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ──────────────────────────────────────────────
        // Person Configuration
        // ──────────────────────────────────────────────
        modelBuilder.Entity<Person>(entity =>
        {
            entity.ToTable("Persons");

            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.ExternalId).IsUnique().HasFilter("[ExternalId] IS NOT NULL");
            entity.HasIndex(e => e.IsActive);

            // Cascade delete: removing a person also removes their face embeddings
            entity.HasMany(e => e.FaceEmbeddings)
                  .WithOne(e => e.Person)
                  .HasForeignKey(e => e.PersonId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ──────────────────────────────────────────────
        // FaceEmbedding Configuration — THE KEY PART
        // ──────────────────────────────────────────────
        modelBuilder.Entity<FaceEmbedding>(entity =>
        {
            entity.ToTable("FaceEmbeddings");

            // ⭐ Map float[] to SQL Server 2025 native VECTOR(512) type.
            // This is what enables VECTOR_DISTANCE() in T-SQL queries.
            // 
            // The EFCore.SqlServer.VectorSearch plugin translates:
            //   C#:  EF.Functions.VectorDistance("cosine", e.Embedding, queryVector)
            //   SQL: VECTOR_DISTANCE('cosine', [Embedding], @p0)
            entity.Property(e => e.Embedding)
                  .HasColumnType("vector(512)");

            entity.HasIndex(e => e.PersonId);
        });

        // ──────────────────────────────────────────────
        // RecognitionLog Configuration
        // ──────────────────────────────────────────────
        modelBuilder.Entity<RecognitionLog>(entity =>
        {
            entity.ToTable("RecognitionLogs");

            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.PersonId);
            entity.HasIndex(e => e.WasRecognized);

            // Don't cascade: if a person is deleted, keep the logs
            // (set PersonId to NULL via SetNull)
            entity.HasOne(e => e.Person)
                  .WithMany()
                  .HasForeignKey(e => e.PersonId)
                  .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
