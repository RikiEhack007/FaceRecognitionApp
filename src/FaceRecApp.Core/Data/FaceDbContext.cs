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
///   - Biometrics (unified face + fingerprint, with VECTOR(512) for face)
///   - Visits (service routing)
///   - RecognitionLogs (audit trail)
/// </summary>
public class FaceDbContext : DbContext
{
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Biometric> Biometrics => Set<Biometric>();
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

            // Cascade delete: removing a patient also removes their biometrics
            entity.HasMany(e => e.Biometrics)
                  .WithOne(e => e.Patient)
                  .HasForeignKey(e => e.PID)
                  .OnDelete(DeleteBehavior.Cascade);

            // Cascade delete: removing a patient also removes their visits
            entity.HasMany(e => e.Visits)
                  .WithOne(e => e.Patient)
                  .HasForeignKey(e => e.PID)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ──────────────────────────────────────────────
        // Biometric Configuration (unified face + fingerprint)
        // ──────────────────────────────────────────────
        modelBuilder.Entity<Biometric>(entity =>
        {
            entity.ToTable("Biometrics");

            // Map float[] to SQL Server 2025 native VECTOR(512) type for face embeddings.
            entity.Property(e => e.Embedding)
                  .HasColumnType("vector(512)");

            entity.HasIndex(e => e.PID);
            entity.HasIndex(e => e.BiometricType);
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

            // Don't cascade: if a patient is deleted, keep the logs
            entity.HasOne(e => e.Patient)
                  .WithMany()
                  .HasForeignKey(e => e.PID)
                  .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
