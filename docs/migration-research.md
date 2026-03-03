# Database Migration Schema Research Report

> **Date:** 2026-03-03
> **Scope:** Entity models, EF Core migrations, FK relationships, cascade behaviors, template storage, vector indexing, and data access patterns

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Migration History](#2-migration-history)
3. [Entity Models](#3-entity-models)
4. [Fluent API Configuration (FaceDbContext)](#4-fluent-api-configuration)
5. [Relationship Diagram](#5-relationship-diagram)
6. [Cascade Delete Behaviors](#6-cascade-delete-behaviors)
7. [Template Storage: Face vs. Fingerprint](#7-template-storage-face-vs-fingerprint)
8. [Vector Indexing (DiskANN)](#8-vector-indexing-diskann)
9. [Repository Data Access Patterns](#9-repository-data-access-patterns)
10. [Complete Column Reference](#10-complete-column-reference)
11. [Storage & Capacity Analysis](#11-storage--capacity-analysis)
12. [Known Issues & Design Decisions](#12-known-issues--design-decisions)

---

## 1. Executive Summary

The database schema spans **5 tables** (`Patients`, `FaceEmbeddings`, `Biometrics`, `Visits`, `RecognitionLogs`) built across **2 EF Core migrations**. It uses SQL Server 2025's native `VECTOR(512)` type for face embeddings and `varbinary(max)` for fingerprint templates, with a unified `Biometrics` table linking both modalities to patients.

Key architectural characteristics:
- **Two-migration evolution:** Simple person recognition (M1) → comprehensive patient identification system (M2)
- **Multimodal biometrics:** Face embeddings stored as `VECTOR(512)` in `FaceEmbeddings`; fingerprint templates stored as `varbinary(max)` in `Biometrics`
- **Cascade delete management:** Careful FK configuration to avoid SQL Server's multiple cascade path restriction
- **Soft-delete pattern:** `IsActive` flag on `Patients` for non-destructive deactivation
- **Audit trail preservation:** `RecognitionLogs` use `SetNull` FK behavior to retain logs when patients are deleted
- **Optional DiskANN indexing:** Graph-based approximate nearest neighbor search for 5,000+ embedding scale

---

## 2. Migration History

### Migration 1: `20260219073429_InitialCreate`

**File:** `src/FaceRecApp.Core/Data/Migrations/20260219073429_InitialCreate.cs`

Creates the foundational 3-table schema:

| Table | Purpose |
|-------|---------|
| `Persons` | Registered individuals (later renamed to `Patients`) |
| `FaceEmbeddings` | 512-dim ArcFace vectors stored as `vector(512)` |
| `RecognitionLogs` | Audit trail of recognition attempts |

**Persons table (original):**
- `Id` (int, PK, identity)
- `Name` (nvarchar(100), NOT NULL)
- `Notes` (nvarchar(500), nullable)
- `ExternalId` (nvarchar(50), nullable, unique sparse index)
- `CreatedAt`, `LastSeenAt` (datetime2)
- `TotalRecognitions` (int)
- `IsActive` (bit)

**Indexes created:**
- `IX_Persons_ExternalId` — UNIQUE WHERE ExternalId IS NOT NULL (sparse)
- `IX_Persons_IsActive` — soft-delete filter queries
- `IX_Persons_Name` — search/display

**FaceEmbeddings table:**
- `Id` (int, PK, identity)
- `PersonId` (int, FK → Persons, Cascade)
- `Embedding` (`vector(512)`) — the core vector column
- `FaceThumbnail` (varbinary(max), nullable)
- `CaptureAngle` (nvarchar(20), nullable)
- `QualityScore` (real, nullable)
- `CapturedAt` (datetime2)

**RecognitionLogs table:**
- `Id` (int, PK, identity)
- `PersonId` (int?, FK → Persons, **SetNull**)
- `Distance` (real — cosine distance 0.0–1.0)
- `WasRecognized` (bit)
- `PassedLiveness` (bit)
- `StationId` (nvarchar(50), nullable)
- `Timestamp` (datetime2)

### Migration 2: `20260302162748_PatientIdentificationSystem`

**File:** `src/FaceRecApp.Core/Data/Migrations/20260302162748_PatientIdentificationSystem.cs`

Major structural refactoring that transforms the schema into a comprehensive patient management system.

**Key changes:**

1. **Table rename:** `Persons` → `Patients`
2. **Column rename:** `Name` → `FullName`
3. **24 new columns** added to `Patients` (demographics, address, family, consent, audit)
4. **2 new tables:** `Biometrics` and `Visits`
5. **IDCard backfill:** `'X' + RIGHT('00000' + CAST(Id AS VARCHAR(5)), 5)` (e.g., "X00001")

**New columns on Patients:**

| Category | Columns |
|----------|---------|
| Site Management | `Site` (nvarchar(10)) |
| Identification | `IDCard` (nvarchar(10), REQUIRED, UNIQUE) |
| Demographics | `DOBYear`, `DOBMonth`, `DOBDay` (short?), `AgeAtEnrolment`, `MonthAtEnrolment`, `DayAtEnrolment` (byte?), `Sex` (byte?) |
| Address | `AddressCode` (nvarchar(50)), `AddressOther` (nvarchar(max)) |
| Family | `MotherPID` (nvarchar(10)), `MotherName` (nvarchar(255)), `FatherName` (nvarchar(255)), `SpouseName` (nvarchar(100)) |
| Consent | `ConsentGiven` (bit), `ConsentDate` (datetime2?) |
| Audit | `CreatedBy`, `ModifiedBy` (nvarchar(50)), `ModifiedDate` (datetime2?), `LastSync` (datetime2?), `AdmissionDate` (datetime2?) |

**New table: Biometrics**

```
Biometrics
├─ Id (int, PK, Identity)
├─ PersonId (int, FK → Patients, Cascade)
├─ FaceEmbeddingId (int?, FK → FaceEmbeddings, NoAction)
├─ CaptureDate (datetime2)
├─ BiometricType (nvarchar(20), REQUIRED)
├─ Template (varbinary(max)?)
├─ Remark (nvarchar(100)?)
├─ Consent (bit)
├─ ConsentRefusalReason (nvarchar(500)?)
├─ CreatedBy (nvarchar(50)?)
├─ ModifiedBy (nvarchar(50)?)
└─ ModifiedDate (datetime2?)
```

**New table: Visits**

```
Visits
├─ Id (int, PK, Identity)
├─ PersonId (int, FK → Patients, Cascade)
├─ VisitDate (datetime2)
├─ ChiefComplaint (nvarchar(500)?)
├─ ServiceType (nvarchar(50), REQUIRED)
├─ CreatedBy (nvarchar(50)?)
├─ ModifiedBy (nvarchar(50)?)
└─ ModifiedDate (datetime2?)
```

---

## 3. Entity Models

### Person.cs → `Patients` table

**File:** `src/FaceRecApp.Core/Entities/Person.cs`

```csharp
public class Person
{
    // Primary key
    public int Id { get; set; }

    // Patient identification
    [MaxLength(10)] public string? Site { get; set; }
    [Required][MaxLength(10)] public string IDCard { get; set; } = string.Empty;
    public DateTime? AdmissionDate { get; set; }

    // Demographics
    [Required][MaxLength(100)] public string FullName { get; set; } = string.Empty;
    public byte? Sex { get; set; }              // 1=Male, 2=Female
    public short? DOBYear { get; set; }
    public short? DOBMonth { get; set; }        // -1 = unknown
    public short? DOBDay { get; set; }          // -1 = unknown
    public byte? AgeAtEnrolment { get; set; }
    public byte? MonthAtEnrolment { get; set; }
    public byte? DayAtEnrolment { get; set; }

    // Address
    [MaxLength(50)] public string? AddressCode { get; set; }
    public string? AddressOther { get; set; }

    // Family relationships
    [MaxLength(10)] public string? MotherPID { get; set; }
    [MaxLength(255)] public string? MotherName { get; set; }
    [MaxLength(255)] public string? FatherName { get; set; }
    [MaxLength(100)] public string? SpouseName { get; set; }

    // Consent
    public bool ConsentGiven { get; set; }
    public DateTime? ConsentDate { get; set; }

    // Audit
    [MaxLength(500)] public string? Notes { get; set; }
    [MaxLength(50)] public string? ExternalId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public int TotalRecognitions { get; set; } = 0;
    public bool IsActive { get; set; } = true;
    [MaxLength(50)] public string? CreatedBy { get; set; }
    [MaxLength(50)] public string? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public DateTime? LastSync { get; set; }

    // Navigation properties
    public ICollection<FaceEmbedding> FaceEmbeddings { get; set; } = new List<FaceEmbedding>();
    public ICollection<Biometric> Biometrics { get; set; } = new List<Biometric>();
    public ICollection<Visit> Visits { get; set; } = new List<Visit>();
}
```

### FaceEmbedding.cs → `FaceEmbeddings` table

**File:** `src/FaceRecApp.Core/Entities/FaceEmbedding.cs`

```csharp
public class FaceEmbedding
{
    [Key] public int Id { get; set; }
    public int PersonId { get; set; }
    [ForeignKey(nameof(PersonId))] public Person Person { get; set; } = null!;

    // 512-dim ArcFace vector → SQL Server VECTOR(512)
    public float[] Embedding { get; set; } = Array.Empty<float>();

    public byte[]? FaceThumbnail { get; set; }           // ~5-10 KB JPEG
    [MaxLength(20)] public string? CaptureAngle { get; set; }
    public float? QualityScore { get; set; }
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
}
```

### Biometric.cs → `Biometrics` table

**File:** `src/FaceRecApp.Core/Entities/Biometric.cs`

```csharp
public class Biometric
{
    [Key] public int Id { get; set; }

    // FK to Patient (Cascade)
    public int PersonId { get; set; }
    [ForeignKey(nameof(PersonId))] public Person Person { get; set; } = null!;

    // Optional FK to FaceEmbedding (NoAction — see Section 6)
    public int? FaceEmbeddingId { get; set; }
    [ForeignKey(nameof(FaceEmbeddingId))] public FaceEmbedding? FaceEmbedding { get; set; }

    public DateTime CaptureDate { get; set; } = DateTime.UtcNow;
    [Required][MaxLength(20)] public string BiometricType { get; set; } = "Face";
    public byte[]? Template { get; set; }               // Fingerprint template bytes
    [MaxLength(100)] public string? Remark { get; set; }
    public bool Consent { get; set; }
    [MaxLength(500)] public string? ConsentRefusalReason { get; set; }
    [MaxLength(50)] public string? CreatedBy { get; set; }
    [MaxLength(50)] public string? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
```

### RecognitionLog.cs → `RecognitionLogs` table

**File:** `src/FaceRecApp.Core/Entities/RecognitionLog.cs`

```csharp
public class RecognitionLog
{
    [Key] public int Id { get; set; }
    public int? PersonId { get; set; }                    // NULL if unrecognized
    [ForeignKey(nameof(PersonId))] public Person? Person { get; set; }

    public float Distance { get; set; }                   // Cosine distance 0.0–1.0
    [NotMapped] public float Similarity => 1f - Distance; // Computed
    public bool WasRecognized { get; set; }
    public bool PassedLiveness { get; set; }
    [MaxLength(50)] public string? StationId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
```

### Visit.cs → `Visits` table

**File:** `src/FaceRecApp.Core/Entities/Visit.cs`

```csharp
public class Visit
{
    [Key] public int Id { get; set; }
    public int PersonId { get; set; }
    [ForeignKey(nameof(PersonId))] public Person Person { get; set; } = null!;

    public DateTime VisitDate { get; set; } = DateTime.UtcNow;
    [MaxLength(500)] public string? ChiefComplaint { get; set; }
    [Required][MaxLength(50)] public string ServiceType { get; set; } = string.Empty;
    [MaxLength(50)] public string? CreatedBy { get; set; }
    [MaxLength(50)] public string? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
```

### BiometricRemarks.cs — Constants

**File:** `src/FaceRecApp.Core/Helpers/BiometricRemarks.cs`

| Constant Group | Values |
|----------------|--------|
| `Types.Face` | `"Face"` |
| `Types.FingerL1` – `FingerL5` | `"FingerL1"` – `"FingerL5"` (left hand) |
| `Types.FingerR1` – `FingerR5` | `"FingerR1"` – `"FingerR5"` (right hand) |
| `FingerprintRemarks` | Physical Deformity, Occupational Wear, Temporary Injury, Skin Condition, Elderly/Thin Skin, Equipment Issue, Patient Refusal |
| `FaceRemarks` | Severe Facial Trauma, Post-Surgery (Bandage), Medical Equipment, Uncooperative |
| `ServiceTypes` | OPD, ANC, Vaccine, Study, Follow Up |
| `SexOptions` | 1=Male, 2=Female |

---

## 4. Fluent API Configuration

**File:** `src/FaceRecApp.Core/Data/FaceDbContext.cs`

### DbSet declarations:

```csharp
public DbSet<Person> Persons => Set<Person>();
public DbSet<FaceEmbedding> FaceEmbeddings => Set<FaceEmbedding>();
public DbSet<RecognitionLog> RecognitionLogs => Set<RecognitionLog>();
public DbSet<Biometric> Biometrics => Set<Biometric>();
public DbSet<Visit> Visits => Set<Visit>();
```

### Person configuration:

```csharp
entity.ToTable("Patients");
entity.HasIndex(e => e.FullName);
entity.HasIndex(e => e.IDCard).IsUnique();
entity.HasIndex(e => e.ExternalId).IsUnique().HasFilter("[ExternalId] IS NOT NULL");
entity.HasIndex(e => e.IsActive);
entity.HasIndex(e => e.Site);

entity.HasMany(p => p.FaceEmbeddings).WithOne(e => e.Person)
      .HasForeignKey(e => e.PersonId).OnDelete(DeleteBehavior.Cascade);
entity.HasMany(p => p.Biometrics).WithOne(b => b.Person)
      .HasForeignKey(b => b.PersonId).OnDelete(DeleteBehavior.Cascade);
entity.HasMany(p => p.Visits).WithOne(v => v.Person)
      .HasForeignKey(v => v.PersonId).OnDelete(DeleteBehavior.Cascade);
```

### FaceEmbedding configuration:

```csharp
entity.Property(e => e.Embedding).HasColumnType("vector(512)");
entity.HasIndex(e => e.PersonId);
```

### Biometric configuration:

```csharp
entity.HasIndex(b => b.PersonId);
entity.HasIndex(b => b.BiometricType);
entity.HasOne(b => b.FaceEmbedding).WithMany()
      .HasForeignKey(b => b.FaceEmbeddingId)
      .OnDelete(DeleteBehavior.NoAction);  // Prevents cascade conflict
```

### RecognitionLog configuration:

```csharp
entity.HasIndex(e => e.Timestamp);
entity.HasIndex(e => e.PersonId);
entity.HasIndex(e => e.WasRecognized);
entity.HasOne(e => e.Person).WithMany()
      .HasForeignKey(e => e.PersonId)
      .OnDelete(DeleteBehavior.SetNull);  // Audit trail preservation
```

### Visit configuration:

```csharp
entity.HasIndex(v => v.PersonId);
entity.HasIndex(v => v.VisitDate);
entity.HasIndex(v => v.ServiceType);
```

---

## 5. Relationship Diagram

```
┌──────────────────────────────────────────────────────────┐
│                    PATIENTS (Person)                     │
│  Id | IDCard | FullName | Site | Sex | IsActive | ...    │
└──────────────────────────────────────────────────────────┘
       │              │              │              │
       │(1:N          │(1:N          │(1:N          │(1:N
       │ Cascade)     │ Cascade)     │ Cascade)     │ SetNull)
       │              │              │              │
       ▼              ▼              ▼              ▼
┌──────────────┐ ┌──────────┐ ┌──────────┐ ┌─────────────────┐
│FACEEMBEDDINGS│ │BIOMETRICS│ │  VISITS  │ │RECOGNITIONLOGS  │
│              │ │          │ │          │ │                 │
│ Id           │ │ Id       │ │ Id       │ │ Id              │
│ PersonId(FK) │ │PersonId  │ │PersonId  │ │ PersonId?(FK)   │
│ Embedding    │ │FaceEmb   │ │VisitDate │ │ Distance        │
│  VECTOR(512) │ │eddingId? │ │Service   │ │ WasRecognized   │
│ FaceThumbnail│ │  (FK,    │ │Type      │ │ PassedLiveness  │
│ CaptureAngle │ │NoAction) │ │Chief     │ │ StationId       │
│ QualityScore │ │Biometric │ │Complaint │ │ Timestamp       │
│ CapturedAt   │ │Type      │ │CreatedBy │ └─────────────────┘
└──────────────┘ │Template  │ └──────────┘
       ▲         │ (varbinay│
       │         │  (max))  │
       │         │Remark    │
       │ (FK,    │Consent   │
       │NoAction)│CreatedBy │
       └─────────┘──────────┘
```

**Critical path:** `Biometric.FaceEmbeddingId` → `FaceEmbedding.Id` uses **NoAction** (not Cascade or SetNull) to avoid SQL Server's multiple cascade path restriction. This requires manual cleanup in code before deleting a Patient.

---

## 6. Cascade Delete Behaviors

### FK Behavior Matrix

| Source | Target | FK Column | Behavior | Rationale |
|--------|--------|-----------|----------|-----------|
| Patient | FaceEmbedding | PersonId | **Cascade** | Remove all embeddings when patient deleted |
| Patient | Biometric | PersonId | **Cascade** | Remove capture records when patient deleted |
| Patient | Visit | PersonId | **Cascade** | Remove visit history when patient deleted |
| Patient | RecognitionLog | PersonId | **SetNull** | Preserve audit trail (PersonId becomes NULL) |
| Biometric | FaceEmbedding | FaceEmbeddingId | **NoAction** | Prevent cascade conflict (see below) |

### The Cascade Conflict Problem

SQL Server prohibits multiple cascade paths to the same table. Without the NoAction workaround:

```
Path 1: Patient ──Cascade──→ FaceEmbedding (direct)
Path 2: Patient ──Cascade──→ Biometric ──Cascade──→ FaceEmbedding (indirect)
```

Both paths would attempt to cascade-delete `FaceEmbedding` rows, which SQL Server rejects with error:

> *"Introducing FOREIGN KEY constraint may cause cycles or multiple cascade paths."*

**Solution:** `Biometric.FaceEmbeddingId` uses `DeleteBehavior.NoAction`. The repository manually nulls out `Biometric.FaceEmbeddingId` before deleting a Patient:

```csharp
// FaceRepository.DeletePersonAsync()
var person = await db.Persons
    .Include(p => p.FaceEmbeddings)
    .Include(p => p.Biometrics)    // Must include to null out FKs
    .FirstOrDefaultAsync(p => p.Id == personId);

if (person != null)
{
    // Clear FK references to avoid NoAction constraint violation
    foreach (var biometric in person.Biometrics)
        biometric.FaceEmbeddingId = null;

    db.Persons.Remove(person);     // Cascade handles the rest
    await db.SaveChangesAsync();
}
```

---

## 7. Template Storage: Face vs. Fingerprint

The system uses a **dual-storage strategy** for biometric data:

### Face Biometrics

| Aspect | Detail |
|--------|--------|
| **Storage table** | `FaceEmbeddings` |
| **Column** | `Embedding` |
| **SQL type** | `vector(512)` (native SQL Server 2025) |
| **.NET type** | `float[]` (512 elements) |
| **Size** | 512 floats × 4 bytes = **2,048 bytes** per embedding |
| **Search** | SQL `VECTOR_DISTANCE('cosine', ...)` or `VECTOR_SEARCH()` TVF |
| **Link to Biometrics** | `Biometric.FaceEmbeddingId` (optional FK) |
| **Thumbnail** | `FaceThumbnail` (varbinary(max), ~5-10 KB JPEG) |

### Fingerprint Biometrics

| Aspect | Detail |
|--------|--------|
| **Storage table** | `Biometrics` |
| **Column** | `Template` |
| **SQL type** | `varbinary(max)` |
| **.NET type** | `byte[]?` |
| **Size** | Typically **1-2 KB** per merged template (ZK9500 SDK) |
| **Search** | ZK SDK in-memory cache (`DBIdentify`), NOT SQL vector search |
| **Link to FaceEmbeddings** | None (FaceEmbeddingId is NULL for fingerprints) |

### Why Different Storage?

Face embeddings are **mathematical vectors** (512 floats) designed for cosine similarity computation. SQL Server 2025's native `VECTOR` type enables hardware-accelerated distance calculations and DiskANN approximate nearest neighbor indexing.

Fingerprint templates are **opaque binary blobs** generated by the ZK fingerprint SDK. They cannot be meaningfully compared using SQL vector operations — the SDK's proprietary matching algorithm is required. Templates are loaded into the SDK's in-memory cache (`DBAdd`) at runtime and matched using `DBIdentify` (1:N) or `DBMatch` (1:1).

### Biometric Record Types

```
BiometricType = "Face":
  → FaceEmbeddingId links to FaceEmbeddings.Id
  → Template is NULL
  → Face data lives in FaceEmbeddings table

BiometricType = "FingerL1" through "FingerR5":
  → FaceEmbeddingId is NULL
  → Template contains SDK-generated merged bytes
  → Fingerprint data lives in Biometrics.Template

BiometricType = any (with Remark set):
  → FaceEmbeddingId is NULL, Template is NULL
  → Remark explains why capture failed
```

---

## 8. Vector Indexing (DiskANN)

### Optional Index Creation

From `scripts/02_advanced_optimizations.sql`:

```sql
ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;
GO

CREATE VECTOR INDEX IX_FaceEmbeddings_Vector
ON FaceEmbeddings(Embedding)
WITH (METRIC = 'cosine', TYPE = 'diskann');
GO
```

### Search Path Selection

The repository detects the index at runtime and switches execution paths:

```csharp
// Startup detection
public async Task DetectVectorIndexAsync()
{
    var count = await db.Database.SqlQueryRaw<int>(
        @"SELECT COUNT(*) FROM sys.indexes i
          WHERE i.name = 'IX_FaceEmbeddings_Vector'
          AND i.is_disabled = 0"
    ).FirstOrDefaultAsync();

    _useVectorSearch = count > 0;
}
```

**Path 1 — DiskANN (approximate, fast):**

```sql
VECTOR_SEARCH(
    TABLE = dbo.FaceEmbeddings AS t,
    COLUMN = Embedding,
    SIMILAR_TO = @qv,
    METRIC = 'cosine',
    TOP_N = 10
)
```

**Path 2 — Brute-force (exact, slower):**

```csharp
db.FaceEmbeddings
    .Select(e => new {
        Embedding = e,
        Distance = EF.Functions.VectorDistance("cosine", e.Embedding, queryEmbedding)
    })
    .OrderBy(x => x.Distance)
    .FirstOrDefaultAsync();
```

### Performance Characteristics

| Scale | Exact Search | DiskANN |
|-------|-------------|---------|
| 1,000 | <1ms | N/A |
| 10,000 | ~10ms | ~2ms |
| 100,000 | ~100ms | ~3ms |
| 500,000 | ~500ms | ~5ms |

### SQL Server 2025 Preview Limitation

The DiskANN vector index has an important limitation in the current SQL Server 2025 preview: **the table becomes INSERT-ONLY while the index exists**. To UPDATE or DELETE rows, the index must be dropped, modifications made, then the index recreated. This limitation may be removed in future releases.

---

## 9. Repository Data Access Patterns

**File:** `src/FaceRecApp.Core/Services/FaceRepository.cs` (~864 lines)

### Thread Safety

Uses `IDbContextFactory<FaceDbContext>` for thread-safe, short-lived DbContext instances:

```csharp
public class FaceRepository
{
    private readonly IDbContextFactory<FaceDbContext> _dbFactory;
}
```

Each method creates its own `DbContext` via `await _dbFactory.CreateDbContextAsync()`, ensuring concurrent operations don't conflict.

### Enrollment Flow

```
User captures 3-5 face images
    → FaceDetectionService (SCRFD) → bounding boxes
    → FaceRecognitionService (ArcFace) → float[512] per image
    → RegisterPersonAsync(name, embeddings[])
        ├─ Creates Person entity
        ├─ Creates FaceEmbedding per sample (angle: front, left, right...)
        └─ SaveChangesAsync() → EF Core inserts via FK cascade
```

### Recognition Flow (1:N Search)

```
Camera frame every 6th → RecognitionPipeline
    → GenerateEmbedding() → float[512]
    → FindClosestMatchAsync(embedding)
        ├─ _useVectorSearch = true → VECTOR_SEARCH TVF (DiskANN)
        └─ _useVectorSearch = false → ORDER BY VECTOR_DISTANCE (brute-force)
    → Filter: IsActive = true, Distance ≤ 0.55
    → Return FaceMatchResult { Person, Distance, IsMatch, IsHighConfidence }
```

### Verification Flow (1:1)

```
User selects patient → captures face
    → VerifyAgainstPatientAsync(personId, embedding)
        └─ Query ONLY this patient's FaceEmbeddings
           ORDER BY VECTOR_DISTANCE ASC
           → Return best match with IsMatch flag
```

### Fingerprint Flow

```
Enrollment:
    → AddFingerprintTemplateAsync(personId, fingerType, mergedTemplate, consent)
        └─ Inserts Biometric { BiometricType="FingerR2", Template=bytes }

Search:
    → GetAllFingerprintTemplatesAsync()
        └─ SELECT Id, Template, PersonId FROM Biometrics
           WHERE Template IS NOT NULL AND BiometricType LIKE 'Finger%'
    → Load into SDK in-memory cache (Biometric.Id as FID)
    → SDK.DBIdentify() → returns FID → resolve to PersonId
```

### Key Repository Methods

| Method | Purpose | Tables Touched |
|--------|---------|----------------|
| `RegisterPersonAsync()` | Full enrollment with embeddings | Patients, FaceEmbeddings |
| `FindClosestMatchAsync()` | 1:N face search | FaceEmbeddings, Patients |
| `VerifyAgainstPatientAsync()` | 1:1 face verification | FaceEmbeddings, Patients |
| `DeletePersonAsync()` | Hard delete with FK cleanup | Patients, FaceEmbeddings, Biometrics |
| `AddFingerprintTemplateAsync()` | Store fingerprint template | Biometrics |
| `GetAllFingerprintTemplatesAsync()` | Load all fingerprint templates | Biometrics |
| `AddBiometricRecordAsync()` | Add face biometric record | Biometrics |
| `LogRecognitionAsync()` | Audit trail entry | RecognitionLogs, Patients |
| `SearchPatientsByNameAsync()` | Patient name search | Patients |
| `GetPatientByPidAsync()` | Full patient load by IDCard | Patients, FaceEmbeddings, Biometrics, Visits |

---

## 10. Complete Column Reference

### Patients Table

| Column | SQL Type | Nullable | Constraints | Purpose |
|--------|----------|----------|-------------|---------|
| Id | int | N | PK, Identity(1,1) | Primary key |
| FullName | nvarchar(100) | N | NOT NULL | Display name |
| IDCard | nvarchar(10) | N | UNIQUE, NOT NULL | Patient ID ("X00001") |
| Site | nvarchar(10) | Y | Index | Hospital site code |
| Sex | tinyint | Y | {1,2} | 1=Male, 2=Female |
| DOBYear | smallint | Y | | Birth year |
| DOBMonth | smallint | Y | | 1-12 or -1 (unknown) |
| DOBDay | smallint | Y | | 1-31 or -1 (unknown) |
| AgeAtEnrolment | tinyint | Y | | Age at registration |
| MonthAtEnrolment | tinyint | Y | | Age month component |
| DayAtEnrolment | tinyint | Y | | Age day component |
| AddressCode | nvarchar(50) | Y | | Address ID |
| AddressOther | nvarchar(max) | Y | | Free-text address |
| MotherPID | nvarchar(10) | Y | | Mother's IDCard |
| MotherName | nvarchar(255) | Y | | Mother's name |
| FatherName | nvarchar(255) | Y | | Father's name |
| SpouseName | nvarchar(100) | Y | | Spouse's name |
| ConsentGiven | bit | N | Default=0 | Biometric consent |
| ConsentDate | datetime2 | Y | | When consent given |
| Notes | nvarchar(500) | Y | | Notes/remarks |
| ExternalId | nvarchar(50) | Y | UNIQUE (sparse) | External system ID |
| CreatedAt | datetime2 | N | | Registration timestamp |
| LastSeenAt | datetime2 | N | | Last recognition |
| TotalRecognitions | int | N | Default=0 | Recognition count |
| IsActive | bit | N | Index | Soft-delete flag |
| CreatedBy | nvarchar(50) | Y | | Enrollment operator |
| ModifiedBy | nvarchar(50) | Y | | Last modifier |
| ModifiedDate | datetime2 | Y | | Last update time |
| LastSync | datetime2 | Y | | Last external sync |
| AdmissionDate | datetime2 | Y | | Hospital admission |

### FaceEmbeddings Table

| Column | SQL Type | Nullable | Constraints | Purpose |
|--------|----------|----------|-------------|---------|
| Id | int | N | PK, Identity(1,1) | Primary key |
| PersonId | int | N | FK → Patients (Cascade) | Patient reference |
| **Embedding** | **vector(512)** | N | NOT NULL | 512-dim ArcFace vector |
| FaceThumbnail | varbinary(max) | Y | | Face JPEG thumbnail |
| CaptureAngle | nvarchar(20) | Y | | "front", "left", "right" |
| QualityScore | real | Y | | 0.0–1.0 quality |
| CapturedAt | datetime2 | N | | Capture timestamp |

### Biometrics Table

| Column | SQL Type | Nullable | Constraints | Purpose |
|--------|----------|----------|-------------|---------|
| Id | int | N | PK, Identity(1,1) | Primary key |
| PersonId | int | N | FK → Patients (Cascade) | Patient reference |
| FaceEmbeddingId | int | Y | FK → FaceEmbeddings (**NoAction**) | Link to embedding (face only) |
| CaptureDate | datetime2 | N | | Capture timestamp |
| BiometricType | nvarchar(20) | N | NOT NULL, Index | "Face", "FingerL1"–"FingerR5" |
| **Template** | **varbinary(max)** | Y | | Fingerprint template bytes |
| Remark | nvarchar(100) | Y | | Failure reason |
| Consent | bit | N | | Patient consented? |
| ConsentRefusalReason | nvarchar(500) | Y | | Why refused |
| CreatedBy | nvarchar(50) | Y | | Operator |
| ModifiedBy | nvarchar(50) | Y | | Last modifier |
| ModifiedDate | datetime2 | Y | | Last update |

### RecognitionLogs Table

| Column | SQL Type | Nullable | Constraints | Purpose |
|--------|----------|----------|-------------|---------|
| Id | int | N | PK, Identity(1,1) | Primary key |
| PersonId | int | Y | FK → Patients (**SetNull**) | Matched person (NULL if unknown) |
| Distance | real | N | | Cosine distance (0.0–1.0) |
| WasRecognized | bit | N | | Below threshold? |
| PassedLiveness | bit | N | | Passed anti-spoofing? |
| StationId | nvarchar(50) | Y | | Kiosk/terminal ID |
| Timestamp | datetime2 | N | Index | When recognition occurred |

### Visits Table

| Column | SQL Type | Nullable | Constraints | Purpose |
|--------|----------|----------|-------------|---------|
| Id | int | N | PK, Identity(1,1) | Primary key |
| PersonId | int | N | FK → Patients (Cascade) | Patient reference |
| VisitDate | datetime2 | N | Index | Visit date |
| ChiefComplaint | nvarchar(500) | Y | | Chief complaint |
| ServiceType | nvarchar(50) | N | NOT NULL, Index | "OPD", "ANC", "Vaccine", etc. |
| CreatedBy | nvarchar(50) | Y | | Operator |
| ModifiedBy | nvarchar(50) | Y | | Last modifier |
| ModifiedDate | datetime2 | Y | | Last update |

---

## 11. Storage & Capacity Analysis

### Per-Row Storage Estimates

| Table | Estimated Row Size | Notes |
|-------|-------------------|-------|
| Patients | ~800–1,200 bytes | Varies with string field usage |
| FaceEmbeddings | ~2,200–12,200 bytes | 2,048 fixed (vector) + optional thumbnail |
| Biometrics | ~200–5,200 bytes | Fingerprint templates typically 1-2 KB |
| RecognitionLogs | ~50 bytes | Compact audit entries |
| Visits | ~200 bytes | String fields relatively short |

### Capacity at 10,000 Patients (SQL Server Express 50 GB Limit)

**Conservative estimate (3 embeddings, no thumbnails):**

| Table | Rows | Size |
|-------|------|------|
| Patients | 10,000 | ~10 MB |
| FaceEmbeddings | 30,000 | ~66 MB |
| Biometrics | 30,000 | ~15 MB |
| RecognitionLogs | 1,000,000 | ~50 MB |
| **Total** | | **~140 MB (0.3% of 50 GB)** |

**Generous estimate (5 embeddings + thumbnails):**

| Table | Rows | Size |
|-------|------|------|
| Patients | 10,000 | ~10 MB |
| FaceEmbeddings | 50,000 | ~600 MB |
| Biometrics | 50,000 | ~25 MB |
| RecognitionLogs | 10,000,000 | ~500 MB |
| **Total** | | **~1.1 GB (2.2% of 50 GB)** |

SQL Server Express comfortably handles the expected scale.

---

## 12. Known Issues & Design Decisions

### Issue 1: Cascade Delete Requires Manual FK Cleanup

**Impact:** Deleting a patient without first nulling `Biometric.FaceEmbeddingId` throws a FK constraint violation.

**Status:** Fixed in `FaceRepository.DeletePersonAsync()` — includes `Biometrics` and nulls the FK before removing the person.

**Root cause:** SQL Server's prohibition on multiple cascade paths to the same table.

### Issue 2: DiskANN Makes Table INSERT-ONLY

**Impact:** Cannot UPDATE or DELETE `FaceEmbeddings` rows while the DiskANN vector index exists (SQL Server 2025 preview limitation).

**Workaround:** Drop index → modify → recreate, or operate without the index at small scale.

### Issue 3: MotherPID is a Logical FK, Not a Physical FK

`MotherPID` references another patient's `IDCard` value but has no physical FK constraint. This is intentional — the mother may not be enrolled in the system yet.

### Design Decision: Separate FaceEmbeddings and Biometrics Tables

Face embeddings could theoretically live in the `Biometrics.Template` column as serialized bytes. However, they are kept separate because:

1. `VECTOR(512)` type requires a dedicated column for SQL-native distance computation
2. Multiple embeddings per person (3-5 samples at different angles) is the norm for face recognition
3. Fingerprint templates use a completely different matching mechanism (SDK in-memory, not SQL)
4. The `Biometrics` table serves as a **unified audit log** of all biometric capture attempts, including failures (Remark field)

### Design Decision: RecognitionLog FK Uses SetNull

When a patient is deleted, their recognition logs are preserved with `PersonId = NULL`. This maintains the audit trail for compliance purposes — the system can still report "X unknown faces were detected" or "Y recognitions occurred" without identifying the deleted patient.

### Design Decision: IDbContextFactory (Not Injected DbContext)

The repository uses `IDbContextFactory<FaceDbContext>` rather than a directly injected `FaceDbContext`. This is because the camera capture thread and UI thread may both call repository methods concurrently. Each method creates its own short-lived `DbContext`, avoiding threading issues with EF Core's non-thread-safe `DbContext`.

---

## Index Coverage Summary

| Table | Index Name | Columns | Type | Purpose |
|-------|-----------|---------|------|---------|
| Patients | PK_Patients | Id | Clustered PK | Primary key |
| Patients | IX_Patients_FullName | FullName | Non-clustered | Name search |
| Patients | IX_Patients_IDCard | IDCard | Unique | PID lookup |
| Patients | IX_Patients_ExternalId | ExternalId | Unique, sparse | External system link |
| Patients | IX_Patients_IsActive | IsActive | Non-clustered | Soft-delete filter |
| Patients | IX_Patients_Site | Site | Non-clustered | Multi-site queries |
| FaceEmbeddings | PK_FaceEmbeddings | Id | Clustered PK | Primary key |
| FaceEmbeddings | IX_FaceEmbeddings_PersonId | PersonId | Non-clustered | Per-person lookup |
| FaceEmbeddings | IX_FaceEmbeddings_Vector | Embedding | DiskANN (optional) | ANN search |
| Biometrics | PK_Biometrics | Id | Clustered PK | Primary key |
| Biometrics | IX_Biometrics_PersonId | PersonId | Non-clustered | Per-patient records |
| Biometrics | IX_Biometrics_BiometricType | BiometricType | Non-clustered | Face vs. fingerprint |
| Biometrics | IX_Biometrics_FaceEmbeddingId | FaceEmbeddingId | Non-clustered | Embedding link |
| RecognitionLogs | PK_RecognitionLogs | Id | Clustered PK | Primary key |
| RecognitionLogs | IX_RecognitionLogs_PersonId | PersonId | Non-clustered | Audit by person |
| RecognitionLogs | IX_RecognitionLogs_Timestamp | Timestamp | Non-clustered | Time-series analytics |
| RecognitionLogs | IX_RecognitionLogs_WasRecognized | WasRecognized | Non-clustered | Success/failure ratio |
| Visits | PK_Visits | Id | Clustered PK | Primary key |
| Visits | IX_Visits_PersonId | PersonId | Non-clustered | Patient history |
| Visits | IX_Visits_VisitDate | VisitDate | Non-clustered | Time-series |
| Visits | IX_Visits_ServiceType | ServiceType | Non-clustered | Routing analytics |
