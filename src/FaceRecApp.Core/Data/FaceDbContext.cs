using FaceRecApp.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FaceRecApp.Core.Data;

/// <summary>
/// Entity Framework Core database context for the patient identification system.
///
/// Targets SQL Server 2025 with native VECTOR(512) column type for face embeddings.
///
/// Tables:
///   - Patients (IDCard PK, demographics, audit)
///   - FaceEmbeddings (VECTOR(512) for face recognition)
///   - FingerprintTemplates (ZK SDK templates for fingerprint matching)
///   - Visits (service routing)
///   - RecognitionLogs (audit trail)
/// </summary>
public class FaceDbContext : DbContext
{
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<FaceEmbedding> FaceEmbeddings => Set<FaceEmbedding>();
    public DbSet<FingerprintTemplate> FingerprintTemplates => Set<FingerprintTemplate>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<RecognitionLog> RecognitionLogs => Set<RecognitionLog>();

    public FaceDbContext(DbContextOptions<FaceDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ──────────────────────────────────────────────
        // Patient Configuration (IDCard = PK)
        // ──────────────────────────────────────────────
        modelBuilder.Entity<Patient>(entity =>
        {
            entity.ToTable("Patients");
            entity.HasKey(e => e.IDCard);

            entity.HasIndex(e => e.FullName);
            entity.HasIndex(e => e.Site);

            entity.HasMany(e => e.FaceEmbeddings)
                  .WithOne(e => e.Patient)
                  .HasForeignKey(e => e.PID)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.FingerprintTemplates)
                  .WithOne(e => e.Patient)
                  .HasForeignKey(e => e.PID)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Visits)
                  .WithOne(e => e.Patient)
                  .HasForeignKey(e => e.PID)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ──────────────────────────────────────────────
        // FaceEmbedding Configuration
        // ──────────────────────────────────────────────
        modelBuilder.Entity<FaceEmbedding>(entity =>
        {
            entity.ToTable("FaceEmbeddings");

            entity.Property(e => e.Embedding)
                  .HasColumnType("vector(512)");

            entity.HasIndex(e => e.PID);
        });

        // ──────────────────────────────────────────────
        // FingerprintTemplate Configuration
        // ──────────────────────────────────────────────
        modelBuilder.Entity<FingerprintTemplate>(entity =>
        {
            entity.ToTable("FingerprintTemplates");

            entity.HasIndex(e => e.PID);
            entity.HasIndex(e => e.FingerType);
        });

        // ──────────────────────────────────────────────
        // Visit Configuration
        // ──────────────────────────────────────────────
        modelBuilder.Entity<Visit>(entity =>
        {
            entity.ToTable("Visits");

            entity.HasIndex(e => e.PID);
            entity.HasIndex(e => e.Date);
            entity.HasIndex(e => e.ServiceType);
        });

        // ──────────────────────────────────────────────
        // RecognitionLog Configuration
        // ──────────────────────────────────────────────
        modelBuilder.Entity<RecognitionLog>(entity =>
        {
            entity.ToTable("RecognitionLogs");

            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.PID);
            entity.HasIndex(e => e.WasRecognized);

            entity.HasOne(e => e.Patient)
                  .WithMany()
                  .HasForeignKey(e => e.PID)
                  .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
