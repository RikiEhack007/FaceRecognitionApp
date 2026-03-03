using FaceRecApp.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FaceRecApp.Core.Data;

/// <summary>
/// Entity Framework Core database context for the patient identification system.
///
/// Targets SQL Server 2025 with native VECTOR(512) column type for face embeddings.
///
/// Tables:
///   - Patients (registered patients with demographics)
///   - FaceEmbeddings (with VECTOR(512) column)
///   - FingerprintTemplates (fingerprint enrollment data)
///   - Visits (service routing)
///   - RecognitionLogs (audit trail)
/// </summary>
public class FaceDbContext : DbContext
{
    public DbSet<Person> Persons => Set<Person>();
    public DbSet<FaceEmbedding> FaceEmbeddings => Set<FaceEmbedding>();
    public DbSet<RecognitionLog> RecognitionLogs => Set<RecognitionLog>();
    public DbSet<FingerprintTemplate> FingerprintTemplates => Set<FingerprintTemplate>();
    public DbSet<Visit> Visits => Set<Visit>();

    public FaceDbContext(DbContextOptions<FaceDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ──────────────────────────────────────────────
        // Patient Configuration
        // ──────────────────────────────────────────────
        modelBuilder.Entity<Person>(entity =>
        {
            entity.ToTable("Patients");

            entity.HasIndex(e => e.FullName);
            entity.HasIndex(e => e.IDCard).IsUnique();
            entity.HasIndex(e => e.ExternalId).IsUnique().HasFilter("[ExternalId] IS NOT NULL");
            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.Site);

            // Cascade delete: removing a patient also removes their face embeddings
            entity.HasMany(e => e.FaceEmbeddings)
                  .WithOne(e => e.Person)
                  .HasForeignKey(e => e.PersonId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Cascade delete: removing a patient also removes their fingerprint templates
            entity.HasMany(e => e.FingerprintTemplates)
                  .WithOne(e => e.Person)
                  .HasForeignKey(e => e.PersonId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Cascade delete: removing a patient also removes their visits
            entity.HasMany(e => e.Visits)
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

            // Map float[] to SQL Server 2025 native VECTOR(512) type.
            entity.Property(e => e.Embedding)
                  .HasColumnType("vector(512)");

            entity.HasIndex(e => e.PersonId);
        });

        // ──────────────────────────────────────────────
        // FingerprintTemplate Configuration
        // ──────────────────────────────────────────────
        modelBuilder.Entity<FingerprintTemplate>(entity =>
        {
            entity.HasIndex(e => e.PersonId);
            entity.HasIndex(e => e.FingerType);
        });

        // ──────────────────────────────────────────────
        // Visit Configuration
        // ──────────────────────────────────────────────
        modelBuilder.Entity<Visit>(entity =>
        {
            entity.ToTable("Visits");

            entity.HasIndex(e => e.PersonId);
            entity.HasIndex(e => e.VisitDate);
            entity.HasIndex(e => e.ServiceType);
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

            // Don't cascade: if a patient is deleted, keep the logs
            entity.HasOne(e => e.Person)
                  .WithMany()
                  .HasForeignKey(e => e.PersonId)
                  .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
