# Migration Plan: Clean Biometric Schema Redesign

> **Date:** 2026-03-03
> **Goal:** Replace the overloaded `Biometrics` junction table with a clean, separated design where each table has one job.

---

## Problem Summary

The current `Biometrics` table is doing three jobs:

1. **Junction table** — linking Patient ↔ FaceEmbedding via `FaceEmbeddingId`
2. **Data storage** — storing fingerprint templates directly in `Template` column
3. **Audit table** — tracking consent, remarks, and capture events

This causes:
- A cascade delete conflict requiring `NoAction` FK + manual null-out workaround
- Asymmetric storage (face referenced, fingerprint stored inline)
- Possible invalid states (Face type with no `FaceEmbeddingId`, Finger type with `FaceEmbeddingId`)

## Target Design

```
┌──────────────────────────────────────────────────┐
│                    PATIENTS                       │
│  Id │ IDCard │ FullName │ Sex │ IsActive │ ...   │
└──────────────┬───────────────────────────────────┘
               │
    ┌──────────┼──────────────┬──────────────────┐
    │          │              │                  │
    │(1:N)     │(1:N)         │(1:N)             │(1:N)
    │CASCADE   │CASCADE       │CASCADE           │SET NULL
    ▼          ▼              ▼                  ▼
┌───────────┐ ┌────────────┐ ┌──────────┐ ┌───────────────┐
│ FACE      │ │ FINGERPRINT│ │ VISITS   │ │ RECOGNITION   │
│ EMBEDDINGS│ │ TEMPLATES  │ │          │ │ LOGS          │
│           │ │ (NEW)      │ │          │ │               │
│ Embedding │ │ Template   │ │ VisitDate│ │ Distance      │
│ VECTOR    │ │ varbinary  │ │ Service  │ │ WasRecognized │
│ (512)     │ │ FingerType │ │ Type     │ │ Timestamp     │
│ Thumbnail │ │ Consent    │ │          │ │               │
│ Consent*  │ │ Remark     │ │          │ │               │
│ CreatedBy*│ │ CreatedBy  │ │          │ │               │
└───────────┘ └────────────┘ └──────────┘ └───────────────┘

* = new columns added to existing table
```

**Zero cascade conflicts. Zero junction tables. Each table has one job.**

---

## What Changes

| Action | Table/Entity | Detail |
|--------|-------------|--------|
| **DROP** | `Biometrics` | Remove entirely — junction table eliminated |
| **CREATE** | `FingerprintTemplates` | New first-class table for fingerprint data |
| **ALTER** | `FaceEmbeddings` | Add `Consent`, `CreatedBy` columns |
| **ALTER** | `Person` entity | Replace `Biometrics` nav property with `FingerprintTemplates` |
| **DELETE** | `Biometric.cs` | Remove entity class |
| **CREATE** | `FingerprintTemplate.cs` | New entity class |
| **UPDATE** | `FaceDbContext.cs` | Replace Biometric config with FingerprintTemplate config |
| **UPDATE** | `FaceRepository.cs` | Update 5 methods |
| **UPDATE** | `MainViewModel.cs` | Update fingerprint search + facial change logic |
| **UPDATE** | `EnrolmentWindow.xaml.cs` | Update enrollment save logic |
| **UPDATE** | `EnrolmentWindow.xaml` | No XAML changes needed (UI stays the same) |

---

## Data Migration Strategy

Before dropping `Biometrics`, migrate existing data:

| Source (Biometrics) | Destination | Condition |
|--------------------|-----------|----|
| Rows where `BiometricType LIKE 'Finger%'` | `FingerprintTemplates` | Copy Template, FingerType, Consent, Remark, CreatedBy |
| Rows where `BiometricType = 'Face'` AND `FaceEmbeddingId IS NOT NULL` | `FaceEmbeddings` (update) | Copy Consent, CreatedBy to matching FaceEmbedding row |
| Rows where `BiometricType = 'Face'` AND `FaceEmbeddingId IS NULL` | Discarded | Remark-only audit records — append to Person.Notes if desired |

---

## Step-by-Step Implementation

### Step 1: Create `FingerprintTemplate` Entity

**New file:** `src/FaceRecApp.Core/Entities/FingerprintTemplate.cs`

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FaceRecApp.Core.Entities;

/// <summary>
/// Stores a fingerprint enrollment template for a patient.
/// One record per finger (FingerL1–L5, FingerR1–R5).
/// Template is nullable — NULL with Remark set means capture failed.
/// </summary>
public class FingerprintTemplate
{
    [Key]
    public int Id { get; set; }

    // ─── FK to Patient (Cascade delete) ───
    public int PersonId { get; set; }

    [ForeignKey(nameof(PersonId))]
    public Person Person { get; set; } = null!;

    // ─── Fingerprint Data ───

    /// <summary>
    /// Which finger: "FingerL1"–"FingerL5", "FingerR1"–"FingerR5".
    /// Uses constants from BiometricRemarks.Types.
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string FingerType { get; set; } = "FingerR2";

    /// <summary>
    /// Merged enrollment template from ZK SDK (DBMerge of 3 captures).
    /// NULL when capture failed — see Remark for reason.
    /// </summary>
    public byte[]? Template { get; set; }

    // ─── Consent & Audit ───

    public DateTime CaptureDate { get; set; } = DateTime.UtcNow;

    public bool Consent { get; set; }

    /// <summary>
    /// Reason capture failed (e.g., "Physical Deformity", "Equipment Issue").
    /// NULL when capture succeeded.
    /// </summary>
    [MaxLength(100)]
    public string? Remark { get; set; }

    [MaxLength(500)]
    public string? ConsentRefusalReason { get; set; }

    [MaxLength(50)]
    public string? CreatedBy { get; set; }

    [MaxLength(50)]
    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }
}
```

### Step 2: Update `FaceEmbedding` Entity

**File:** `src/FaceRecApp.Core/Entities/FaceEmbedding.cs`

Add two fields at the bottom (before the closing brace):

```csharp
    // ─── Consent & Audit (added in schema cleanup) ───

    /// <summary>Whether patient consented to this face capture.</summary>
    public bool Consent { get; set; } = true;

    [MaxLength(50)]
    public string? CreatedBy { get; set; }
```

### Step 3: Update `Person` Entity

**File:** `src/FaceRecApp.Core/Entities/Person.cs`

Replace the `Biometrics` navigation property:

```csharp
// REMOVE this line:
public ICollection<Biometric> Biometrics { get; set; } = new List<Biometric>();

// ADD this line:
public ICollection<FingerprintTemplate> FingerprintTemplates { get; set; } = new List<FingerprintTemplate>();
```

### Step 4: Delete `Biometric` Entity

**Delete file:** `src/FaceRecApp.Core/Entities/Biometric.cs`

### Step 5: Update `FaceDbContext`

**File:** `src/FaceRecApp.Core/Data/FaceDbContext.cs`

#### 5A. Replace DbSet

```csharp
// REMOVE:
public DbSet<Biometric> Biometrics => Set<Biometric>();

// ADD:
public DbSet<FingerprintTemplate> FingerprintTemplates => Set<FingerprintTemplate>();
```

#### 5B. Replace Person → Biometrics relationship

```csharp
// REMOVE (in Person configuration):
entity.HasMany(e => e.Biometrics)
    .WithOne(e => e.Person)
    .HasForeignKey(e => e.PersonId)
    .OnDelete(DeleteBehavior.Cascade);

// ADD:
entity.HasMany(e => e.FingerprintTemplates)
    .WithOne(e => e.Person)
    .HasForeignKey(e => e.PersonId)
    .OnDelete(DeleteBehavior.Cascade);
```

#### 5C. Replace Biometric entity configuration

```csharp
// REMOVE entire Biometric configuration block:
modelBuilder.Entity<Biometric>(entity =>
{
    entity.ToTable("Biometrics");
    entity.HasIndex(e => e.PersonId);
    entity.HasIndex(e => e.BiometricType);
    entity.HasOne(e => e.FaceEmbedding)
        .WithMany()
        .HasForeignKey(e => e.FaceEmbeddingId)
        .OnDelete(DeleteBehavior.NoAction);
});

// ADD:
modelBuilder.Entity<FingerprintTemplate>(entity =>
{
    entity.HasIndex(e => e.PersonId);
    entity.HasIndex(e => e.FingerType);
});
```

**Note:** No `NoAction` FK workaround needed. `FingerprintTemplate` → `Patient` is a simple Cascade. No second path to `FaceEmbeddings`. The cascade conflict is gone.

### Step 6: Update `FaceRepository`

**File:** `src/FaceRecApp.Core/Services/FaceRepository.cs`

#### 6A. Replace `AddBiometricRecordAsync`

```csharp
// REMOVE:
public async Task<Biometric> AddBiometricRecordAsync(Biometric record) { ... }

// No direct replacement — face audit is handled by FaceEmbedding.Consent/CreatedBy,
// fingerprint audit is handled by the methods below.
```

#### 6B. Replace `AddFingerprintTemplateAsync`

```csharp
// REMOVE old method that creates Biometric

// ADD:
public async Task<FingerprintTemplate> AddFingerprintTemplateAsync(
    int personId, string fingerType, byte[]? template, bool consent,
    string? remark = null)
{
    var record = new FingerprintTemplate
    {
        PersonId = personId,
        FingerType = fingerType,
        Template = template,
        Consent = consent,
        Remark = remark,
        CaptureDate = DateTime.UtcNow,
        CreatedBy = Environment.UserName
    };

    await using var db = await _dbFactory.CreateDbContextAsync();
    db.FingerprintTemplates.Add(record);
    await db.SaveChangesAsync();
    return record;
}
```

#### 6C. Replace `GetAllFingerprintTemplatesAsync`

```csharp
// REMOVE old method that queries Biometrics

// ADD:
public async Task<Dictionary<int, (byte[] Template, int PersonId)>>
    GetAllFingerprintTemplatesAsync()
{
    await using var db = await _dbFactory.CreateDbContextAsync();
    var fingerprints = await db.FingerprintTemplates
        .Where(f => f.Template != null)
        .Select(f => new { f.Id, f.Template, f.PersonId })
        .ToListAsync();

    return fingerprints.ToDictionary(
        f => f.Id,
        f => (f.Template!, f.PersonId));
}
```

#### 6D. Replace `GetPersonByBiometricIdAsync`

```csharp
// REMOVE old method that queries Biometrics

// ADD:
public async Task<Person?> GetPersonByFingerprintIdAsync(int fingerprintId)
{
    await using var db = await _dbFactory.CreateDbContextAsync();
    var fp = await db.FingerprintTemplates
        .Include(f => f.Person)
        .FirstOrDefaultAsync(f => f.Id == fingerprintId);
    return fp?.Person;
}
```

#### 6E. Simplify `DeletePersonAsync`

```csharp
// REMOVE the manual FK null-out workaround:
public async Task DeletePersonAsync(int personId)
{
    await using var db = await _dbFactory.CreateDbContextAsync();
    var person = await db.Persons
        .Include(p => p.FaceEmbeddings)
        // REMOVE: .Include(p => p.Biometrics)
        .FirstOrDefaultAsync(p => p.Id == personId);

    if (person != null)
    {
        // REMOVE: foreach (var biometric in person.Biometrics)
        //             biometric.FaceEmbeddingId = null;

        db.Persons.Remove(person);  // Cascade handles everything cleanly
        await db.SaveChangesAsync();
    }
}
```

The `.Include(p => p.FaceEmbeddings)` is kept only if needed for other logic. If it's just for the delete, even that can be removed — EF Core cascade will handle it at the SQL level.

#### 6F. Update `GetPatientByPidAsync`

```csharp
// CHANGE:
.Include(p => p.Biometrics)
// TO:
.Include(p => p.FingerprintTemplates)
```

#### 6G. Update `RegisterPatientAsync` (if it creates Biometric records)

The current `RegisterPatientAsync` does NOT create Biometric records — it only creates Person + FaceEmbedding. Set consent on the FaceEmbedding directly:

```csharp
var faceEmbedding = new FaceEmbedding
{
    Embedding = embedding,
    FaceThumbnail = thumbnail,
    CaptureAngle = "front",
    CapturedAt = DateTime.UtcNow,
    Consent = true,                  // NEW
    CreatedBy = Environment.UserName // NEW
};
```

### Step 7: Update `MainViewModel`

**File:** `src/FaceRecApp.WPF/ViewModels/MainViewModel.cs`

#### 7A. Update fingerprint match resolution

```csharp
// CHANGE (in ResolveFingerprintMatchAsync or OnFingerprintForSearch):
var person = await repo.GetPersonByBiometricIdAsync(match.Fid);
// TO:
var person = await repo.GetPersonByFingerprintIdAsync(match.Fid);
```

#### 7B. Update facial change remark (lines ~1009-1020)

The current code creates a `Biometric` record for facial change. Replace with appending to `Person.Notes`:

```csharp
// REMOVE:
if (FacialChangeChecked)
{
    var biometric = new Biometric
    {
        PersonId = _selectedPatient.Id,
        BiometricType = "Face",
        Consent = _selectedPatient.ConsentGiven,
        Remark = string.IsNullOrWhiteSpace(FacialChangeReason)
            ? "Facial change noted"
            : FacialChangeReason.Trim(),
        CaptureDate = DateTime.UtcNow
    };
    await repo.AddBiometricRecordAsync(biometric);
}

// ADD:
if (FacialChangeChecked)
{
    var reason = string.IsNullOrWhiteSpace(FacialChangeReason)
        ? "Facial change noted"
        : FacialChangeReason.Trim();
    var note = $"[{DateTime.UtcNow:yyyy-MM-dd}] {reason}";

    _selectedPatient.Notes = string.IsNullOrEmpty(_selectedPatient.Notes)
        ? note
        : $"{_selectedPatient.Notes}\n{note}";
}
```

#### 7C. Remove Biometric using directive

```csharp
// REMOVE (if present):
using FaceRecApp.Core.Entities;  // Only if Biometric was the sole reason
```

### Step 8: Update `EnrolmentWindow.xaml.cs`

**File:** `src/FaceRecApp.WPF/Views/EnrolmentWindow.xaml.cs`

#### 8A. Face enrollment — successful capture (lines ~540-549)

```csharp
// REMOVE:
var biometric = new Biometric
{
    PersonId = savedPatient.Id,
    BiometricType = BiometricRemarks.Types.Face,
    FaceEmbeddingId = savedPatient.FaceEmbeddings.FirstOrDefault()?.Id,
    Consent = true,
    CreatedBy = Environment.UserName,
    CaptureDate = DateTime.UtcNow
};
await _repository.AddBiometricRecordAsync(biometric);

// REPLACE WITH: nothing — FaceEmbedding already has Consent and CreatedBy.
// The FaceEmbedding record IS the audit trail for face capture.
```

#### 8B. Face enrollment — failed capture with remark (lines ~567-576)

```csharp
// REMOVE:
var biometric = new Biometric
{
    PersonId = savedPatient.Id,
    BiometricType = BiometricRemarks.Types.Face,
    Consent = true,
    Remark = remarkText,
    CreatedBy = Environment.UserName,
    CaptureDate = DateTime.UtcNow
};
await _repository.AddBiometricRecordAsync(biometric);

// REPLACE WITH: append remark to person notes
if (!string.IsNullOrWhiteSpace(remarkText))
{
    savedPatient.Notes = string.IsNullOrEmpty(savedPatient.Notes)
        ? $"[Face] {remarkText}"
        : $"{savedPatient.Notes}\n[Face] {remarkText}";
    // Notes will be saved with the patient update
}
```

#### 8C. Fingerprint enrollment — successful capture (lines ~582-587)

```csharp
// CHANGE:
await _repository.AddFingerprintTemplateAsync(
    savedPatient.Id,
    _selectedFingerType,
    _capturedFingerprintTemplate,
    consent: ConsentCheckBox.IsChecked == true);

// No change needed — method signature stays the same, implementation updated in Step 6B.
```

#### 8D. Fingerprint enrollment — failed capture with remark (lines ~592-602)

```csharp
// REMOVE:
var fpBiometric = new Biometric
{
    PersonId = savedPatient.Id,
    BiometricType = _selectedFingerType,
    Consent = ConsentCheckBox.IsChecked == true,
    Remark = fpRemarkText,
    CreatedBy = Environment.UserName,
    CaptureDate = DateTime.UtcNow
};
await _repository.AddBiometricRecordAsync(fpBiometric);

// REPLACE WITH: use updated AddFingerprintTemplateAsync with remark
await _repository.AddFingerprintTemplateAsync(
    savedPatient.Id,
    _selectedFingerType,
    template: null,         // No template — capture failed
    consent: ConsentCheckBox.IsChecked == true,
    remark: fpRemarkText);
```

### Step 9: EF Core Migration

**Generate the migration:**

```bash
dotnet ef migrations add CleanBiometricSchema -p src/FaceRecApp.Core -s src/FaceRecApp.WPF
```

EF Core will auto-generate the migration, but the data migration SQL must be added manually. Edit the generated migration file:

#### 9A. Up() method — add before `DropTable("Biometrics")`

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    // ── 1. Add new columns to FaceEmbeddings ──
    migrationBuilder.AddColumn<bool>(
        name: "Consent",
        table: "FaceEmbeddings",
        type: "bit",
        nullable: false,
        defaultValue: true);

    migrationBuilder.AddColumn<string>(
        name: "CreatedBy",
        table: "FaceEmbeddings",
        type: "nvarchar(50)",
        maxLength: 50,
        nullable: true);

    // ── 2. Create FingerprintTemplates table ──
    migrationBuilder.CreateTable(
        name: "FingerprintTemplates",
        columns: table => new
        {
            Id = table.Column<int>(type: "int", nullable: false)
                .Annotation("SqlServer:Identity", "1, 1"),
            PersonId = table.Column<int>(type: "int", nullable: false),
            FingerType = table.Column<string>(type: "nvarchar(20)",
                maxLength: 20, nullable: false),
            Template = table.Column<byte[]>(type: "varbinary(max)",
                nullable: true),
            CaptureDate = table.Column<DateTime>(type: "datetime2",
                nullable: false),
            Consent = table.Column<bool>(type: "bit", nullable: false),
            Remark = table.Column<string>(type: "nvarchar(100)",
                maxLength: 100, nullable: true),
            ConsentRefusalReason = table.Column<string>(type: "nvarchar(500)",
                maxLength: 500, nullable: true),
            CreatedBy = table.Column<string>(type: "nvarchar(50)",
                maxLength: 50, nullable: true),
            ModifiedBy = table.Column<string>(type: "nvarchar(50)",
                maxLength: 50, nullable: true),
            ModifiedDate = table.Column<DateTime>(type: "datetime2",
                nullable: true)
        },
        constraints: table =>
        {
            table.PrimaryKey("PK_FingerprintTemplates", x => x.Id);
            table.ForeignKey(
                name: "FK_FingerprintTemplates_Patients_PersonId",
                column: x => x.PersonId,
                principalTable: "Patients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        });

    migrationBuilder.CreateIndex(
        name: "IX_FingerprintTemplates_PersonId",
        table: "FingerprintTemplates",
        column: "PersonId");

    migrationBuilder.CreateIndex(
        name: "IX_FingerprintTemplates_FingerType",
        table: "FingerprintTemplates",
        column: "FingerType");

    // ── 3. Migrate data: fingerprints from Biometrics → FingerprintTemplates ──
    migrationBuilder.Sql(@"
        INSERT INTO FingerprintTemplates
            (PersonId, FingerType, Template, CaptureDate, Consent,
             Remark, ConsentRefusalReason, CreatedBy, ModifiedBy, ModifiedDate)
        SELECT
            PersonId, BiometricType, Template, CaptureDate, Consent,
            Remark, ConsentRefusalReason, CreatedBy, ModifiedBy, ModifiedDate
        FROM Biometrics
        WHERE BiometricType LIKE 'Finger%'
    ");

    // ── 4. Migrate data: face consent from Biometrics → FaceEmbeddings ──
    migrationBuilder.Sql(@"
        UPDATE fe
        SET fe.Consent = b.Consent,
            fe.CreatedBy = b.CreatedBy
        FROM FaceEmbeddings fe
        INNER JOIN Biometrics b ON b.FaceEmbeddingId = fe.Id
        WHERE b.BiometricType = 'Face'
          AND b.FaceEmbeddingId IS NOT NULL
    ");

    // ── 5. Preserve face remarks in Person.Notes ──
    migrationBuilder.Sql(@"
        UPDATE p
        SET p.Notes = CASE
            WHEN p.Notes IS NULL OR p.Notes = ''
                THEN '[Face] ' + b.Remark
            ELSE p.Notes + CHAR(10) + '[Face] ' + b.Remark
            END
        FROM Patients p
        INNER JOIN Biometrics b ON b.PersonId = p.Id
        WHERE b.BiometricType = 'Face'
          AND b.Remark IS NOT NULL
          AND b.FaceEmbeddingId IS NULL
    ");

    // ── 6. Drop Biometrics table (all data migrated) ──
    migrationBuilder.DropTable(name: "Biometrics");
}
```

#### 9B. Down() method — rollback

```csharp
protected override void Down(MigrationBuilder migrationBuilder)
{
    // Recreate Biometrics table
    migrationBuilder.CreateTable(
        name: "Biometrics",
        columns: table => new
        {
            Id = table.Column<int>(type: "int", nullable: false)
                .Annotation("SqlServer:Identity", "1, 1"),
            PersonId = table.Column<int>(type: "int", nullable: false),
            FaceEmbeddingId = table.Column<int>(type: "int", nullable: true),
            CaptureDate = table.Column<DateTime>(type: "datetime2", nullable: false),
            BiometricType = table.Column<string>(type: "nvarchar(20)",
                maxLength: 20, nullable: false),
            Template = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
            Remark = table.Column<string>(type: "nvarchar(100)",
                maxLength: 100, nullable: true),
            Consent = table.Column<bool>(type: "bit", nullable: false),
            ConsentRefusalReason = table.Column<string>(type: "nvarchar(500)",
                maxLength: 500, nullable: true),
            CreatedBy = table.Column<string>(type: "nvarchar(50)",
                maxLength: 50, nullable: true),
            ModifiedBy = table.Column<string>(type: "nvarchar(50)",
                maxLength: 50, nullable: true),
            ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
        },
        constraints: table =>
        {
            table.PrimaryKey("PK_Biometrics", x => x.Id);
            table.ForeignKey(
                name: "FK_Biometrics_FaceEmbeddings_FaceEmbeddingId",
                column: x => x.FaceEmbeddingId,
                principalTable: "FaceEmbeddings",
                principalColumn: "Id");
            table.ForeignKey(
                name: "FK_Biometrics_Patients_PersonId",
                column: x => x.PersonId,
                principalTable: "Patients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        });

    // Migrate fingerprint data back
    migrationBuilder.Sql(@"
        INSERT INTO Biometrics
            (PersonId, BiometricType, Template, CaptureDate, Consent,
             Remark, ConsentRefusalReason, CreatedBy, ModifiedBy, ModifiedDate)
        SELECT
            PersonId, FingerType, Template, CaptureDate, Consent,
            Remark, ConsentRefusalReason, CreatedBy, ModifiedBy, ModifiedDate
        FROM FingerprintTemplates
    ");

    migrationBuilder.CreateIndex("IX_Biometrics_PersonId",
        "Biometrics", "PersonId");
    migrationBuilder.CreateIndex("IX_Biometrics_BiometricType",
        "Biometrics", "BiometricType");
    migrationBuilder.CreateIndex("IX_Biometrics_FaceEmbeddingId",
        "Biometrics", "FaceEmbeddingId");

    // Drop new table and columns
    migrationBuilder.DropTable(name: "FingerprintTemplates");
    migrationBuilder.DropColumn(name: "Consent", table: "FaceEmbeddings");
    migrationBuilder.DropColumn(name: "CreatedBy", table: "FaceEmbeddings");
}
```

### Step 10: Update `App.xaml.cs`

No changes needed — `FingerprintService` is registered as a singleton, and `FaceRepository` is transient. Neither depends on the `Biometric` entity type at DI registration time.

### Step 11: Apply Migration

```bash
dotnet ef database update -p src/FaceRecApp.Core -s src/FaceRecApp.WPF
```

---

## Files Changed — Complete Checklist

| # | File | Action | Lines Changed |
|---|------|--------|---------------|
| 1 | `src/FaceRecApp.Core/Entities/FingerprintTemplate.cs` | **CREATE** | ~60 lines (new entity) |
| 2 | `src/FaceRecApp.Core/Entities/Biometric.cs` | **DELETE** | Entire file removed |
| 3 | `src/FaceRecApp.Core/Entities/FaceEmbedding.cs` | **EDIT** | Add `Consent`, `CreatedBy` (~4 lines) |
| 4 | `src/FaceRecApp.Core/Entities/Person.cs` | **EDIT** | Replace `Biometrics` → `FingerprintTemplates` nav prop |
| 5 | `src/FaceRecApp.Core/Data/FaceDbContext.cs` | **EDIT** | Replace DbSet + entity config (~20 lines) |
| 6 | `src/FaceRecApp.Core/Services/FaceRepository.cs` | **EDIT** | Update 6 methods (~50 lines) |
| 7 | `src/FaceRecApp.WPF/ViewModels/MainViewModel.cs` | **EDIT** | Update fingerprint search + facial change (~20 lines) |
| 8 | `src/FaceRecApp.WPF/Views/EnrolmentWindow.xaml.cs` | **EDIT** | Update enrollment save logic (~30 lines) |
| 9 | `src/FaceRecApp.Core/Data/Migrations/...CleanBiometricSchema.cs` | **GENERATED + EDITED** | Migration with data SQL |

**Files NOT changed:**
- `BiometricRemarks.cs` — still needed for `Types.FingerR2`, `FingerprintRemarks[]`, etc.
- `EnrolmentWindow.xaml` — UI layout unchanged
- `FingerprintService.cs` — SDK wrapper unchanged
- `App.xaml.cs` — DI unchanged
- `VisitWindow.xaml.cs` — only uses `BiometricRemarks.ServiceTypes`, unaffected
- `RecognitionLog.cs` — unrelated
- `Visit.cs` — unrelated

---

## Before vs. After Comparison

### Cascade Delete

```
BEFORE (broken — requires manual workaround):
  Patient ──Cascade──→ FaceEmbeddings
  Patient ──Cascade──→ Biometrics ──NoAction──→ FaceEmbeddings
  Code: must null out Biometric.FaceEmbeddingId before delete

AFTER (clean — just works):
  Patient ──Cascade──→ FaceEmbeddings
  Patient ──Cascade──→ FingerprintTemplates
  Code: db.Persons.Remove(person); // done
```

### Face Enrollment

```
BEFORE: Creates FaceEmbedding + Biometric (junction record linking them)
AFTER:  Creates FaceEmbedding only (with Consent + CreatedBy on it)
```

### Fingerprint Enrollment

```
BEFORE: Creates Biometric with Template inline, BiometricType = "FingerR2"
AFTER:  Creates FingerprintTemplate with Template, FingerType = "FingerR2"
```

### Invalid States

```
BEFORE: Biometric can have BiometricType="FingerR2" AND FaceEmbeddingId=5 (invalid)
        Biometric can have BiometricType="Face" AND Template=bytes (invalid)
AFTER:  FingerprintTemplate only has fingerprint fields.
        FaceEmbedding only has face fields.
        Invalid combinations are structurally impossible.
```

---

## Verification

```bash
dotnet build
dotnet test tests/FaceRecApp.Tests
dotnet run --project src/FaceRecApp.WPF/FaceRecApp.WPF.csproj
```

**Manual checklist:**
- [ ] Build succeeds with 0 errors, 0 warnings
- [ ] Existing patients with face embeddings still load correctly
- [ ] Existing patients with fingerprint templates still load correctly
- [ ] New patient enrollment with face capture works
- [ ] New patient enrollment with fingerprint capture (3-touch) works
- [ ] Fingerprint search identifies enrolled patients
- [ ] Patient deletion works without FK errors (no manual null-out needed)
- [ ] Enrollment with face remark (no capture) saves remark to Person.Notes
- [ ] Enrollment with fingerprint remark (no capture) saves FingerprintTemplate with null Template
- [ ] "Start Over" in main window resets all state cleanly

---

## Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| Data loss during migration | High | SQL migration copies ALL fingerprint data before dropping Biometrics |
| Face consent data loss | Low | SQL migration copies consent from Biometric → FaceEmbedding |
| Face remark data loss | Low | SQL migration appends remarks to Person.Notes |
| SDK FID mapping break | Medium | FingerprintTemplate.Id replaces Biometric.Id as FID — new IDs generated by identity. Existing cached FIDs will differ from old Biometric.Id, but cache is rebuilt on each app startup, so no persistent issue |
| Tests fail | Medium | Update test assertions that reference Biometric entity |

**Recommendation:** Back up the database before running the migration.

```bash
# Quick backup before migration
sqlcmd -S localhost\SQLEXPRESS -E -Q "BACKUP DATABASE FaceRecognitionDB TO DISK='C:\temp\FaceRecDB_backup.bak'"
```
