# Face Recognition System -- Implementation Report

**Project:** Offline Desktop Face Recognition System
**Version:** 1.0 (Proof of Concept)
**Platform:** Windows 10/11 Desktop
**Date:** February 2026

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [System Architecture](#2-system-architecture)
3. [Technology Stack](#3-technology-stack)
4. [AI/ML Models](#4-aiml-models)
5. [Processing Pipeline](#5-processing-pipeline)
6. [Liveness & Anti-Spoofing System](#6-liveness--anti-spoofing-system)
7. [Database & Vector Search](#7-database--vector-search)
8. [User Interface](#8-user-interface)
9. [Threading & Concurrency Model](#9-threading--concurrency-model)
10. [Performance Benchmarks](#10-performance-benchmarks)
11. [Configuration & Thresholds](#11-configuration--thresholds)
12. [Testing](#12-testing)
13. [Known Limitations](#13-known-limitations)
14. [Future Considerations](#14-future-considerations)

---

## 1. Executive Summary

This system performs **real-time face detection, identification, and liveness verification** using a standard webcam and CPU -- no cloud services, no GPU, and no internet connection required. It is designed for environments where privacy and data sovereignty are critical (healthcare, secure facilities, offline kiosks).

**Core capabilities:**

- Detect and identify faces in real-time from a live camera feed
- Store and search face embeddings using SQL Server 2025's native vector data type
- Verify that the face belongs to a live person (not a photo, video, or screen)
- Register new individuals with a single face capture
- Audit every recognition attempt with distance scores and liveness results

**Scale tested:** 100,000 face embeddings with ~5ms search latency (DiskANN) or ~75ms (brute-force).

---

## 2. System Architecture

### 2.1 Three-Project Solution

```
FaceRecognitionApp.sln
|
+-- src/FaceRecApp.Core        .NET 9 Class Library (platform-agnostic)
|   Business logic, AI models, database access, image processing.
|   No Windows-specific dependencies. Reusable with MAUI, Blazor, etc.
|
+-- src/FaceRecApp.WPF         .NET 9 WinExe (Windows Presentation Foundation)
|   Desktop UI, camera display, MVVM view models.
|   Depends on Core. Adds BitmapSource, Dispatcher, WriteableBitmap.
|
+-- tests/FaceRecApp.Tests     .NET 9 xUnit
    Unit tests (math, entities) + integration tests (database, vector search).
```

### 2.2 Dependency Direction

```
                    +---------------------+
                    |    FaceRecApp.WPF   |
                    |    (Windows UI)     |
                    +----------+----------+
                               |
                          depends on
                               |
                    +----------v----------+
                    |   FaceRecApp.Core   |
                    |  (Business Logic)   |
                    +----------+----------+
                               |
                          depends on
                               |
                    +----------v----------+
                    |  SQL Server 2025    |
                    |  VECTOR(512) type   |
                    +---------------------+
```

**Core has zero references to WPF.** All Windows-specific adapters (BitmapSource conversion, Dispatcher marshalling, WriteableBitmap rendering) live in the WPF project. This means Core can be reused for a MAUI mobile app, Blazor web app, or console utility without modification.

### 2.3 Project File Structure

```
src/FaceRecApp.Core/
    Data/
        FaceDbContext.cs              EF Core 9 context (VECTOR(512) mapping)
        FaceDbContextFactory.cs       Design-time factory for migrations
    Entities/
        Person.cs                     Registered individual
        FaceEmbedding.cs              512-dim vector + JPEG thumbnail
        RecognitionLog.cs             Audit trail per attempt
        RecognitionSettings.cs        Compile-time thresholds (28 constants)
    Helpers/
        ImageConverter.cs             Mat <-> ImageSharp bridge
        OverlayRenderer.cs            Draw bounding boxes + labels on frames
    Models/
        MiniFASNetV2.onnx             Anti-spoofing model (1.7 MB)
    Services/
        FaceDetectionService.cs       SCRFD face detector
        FaceRecognitionService.cs     ArcFace 512-dim embedding generator
        LivenessService.cs            Blink + movement + texture (623 lines)
        AntiSpoofService.cs           MiniFASNetV2 ML classifier
        CameraService.cs              Webcam capture loop on background thread
        FaceRepository.cs             DB CRUD + vector search (2 paths)
        RecognitionPipeline.cs        Orchestrator (497 lines)
        RecognitionResult.cs          Per-face result DTO
        BenchmarkService.cs           Synthetic data + perf testing

src/FaceRecApp.WPF/
    App.xaml / App.xaml.cs             DI container + startup
    appsettings.json                  Connection string + settings
    Converters/Converters.cs          5 XAML value converters
    Helpers/WpfImageHelper.cs         Mat -> WriteableBitmap (zero-alloc)
    ViewModels/MainViewModel.cs       MVVM with CommunityToolkit.Mvvm
    Views/
        MainWindow.xaml/.cs           Camera feed + results panel
        RegisterWindow.xaml/.cs       Single-capture registration dialog
        DatabaseWindow.xaml/.cs       Person management grid
        BenchmarkWindow.xaml/.cs      Performance testing UI
```

### 2.4 Dependency Injection

All services are wired in `App.xaml.cs` at startup:

| Service | Lifetime | Memory | Reason |
|---------|----------|--------|--------|
| FaceDetectionService | Singleton | ~10 MB | SCRFD ONNX model loaded once |
| FaceRecognitionService | Singleton | ~30 MB | ArcFace ONNX model loaded once |
| LivenessService | Singleton | ~2 MB | Eye state ONNX model + state |
| AntiSpoofService | Singleton | ~1.7 MB | MiniFASNetV2 ONNX model |
| CameraService | Singleton | ~1 MB | Webcam handle |
| RecognitionPipeline | Singleton | -- | Orchestrator (references above) |
| FaceRepository | Transient | ~1 KB | Fresh DbContext per DB operation |
| BenchmarkService | Transient | ~1 KB | On-demand performance testing |

**Total model memory: ~44 MB** (loaded once at startup, lives for app lifetime).

---

## 3. Technology Stack

### 3.1 Runtime & Framework

| Component | Version | Purpose |
|-----------|---------|---------|
| .NET | 9.0 | Cross-platform runtime |
| C# | 13 | Language with nullable reference types, implicit usings |
| WPF | .NET 9 Windows | Desktop UI framework (Windows-only) |
| CommunityToolkit.Mvvm | 8.4.0 | MVVM source generators (`[ObservableProperty]`, `[RelayCommand]`) |

### 3.2 AI / Machine Learning

| Package | Version | Model | Purpose |
|---------|---------|-------|---------|
| FaceAiSharp.Bundle | 0.5.23 | SCRFD | Face detection (boxes + 5-point landmarks) |
| FaceAiSharp.Bundle | 0.5.23 | ArcFace | Face embedding (512-dim float vector) |
| FaceAiSharp.Bundle | 0.5.23 | Eye State | Blink detection (Open/Closed per eye) |
| Microsoft.ML.OnnxRuntime | 1.20.1 | -- | ONNX model inference engine (CPU-only) |
| Custom ONNX | -- | MiniFASNetV2 | Anti-spoofing (real vs phone/print/video) |

### 3.3 Image Processing

| Package | Version | Purpose |
|---------|---------|---------|
| SixLabors.ImageSharp | 3.1.12 | Cross-platform image library (FaceAiSharp's native format) |
| SixLabors.ImageSharp.Drawing | 2.1.7 | Image drawing operations |
| OpenCvSharp4 | 4.10.0 | Webcam capture, frame overlays, Laplacian texture analysis |
| OpenCvSharp4.runtime.win | 4.10.0 | Windows native OpenCV binaries |
| OpenCvSharp4.WpfExtensions | 4.10.0 | Mat to BitmapSource/WriteableBitmap |

### 3.4 Database & ORM

| Component | Version | Purpose |
|-----------|---------|---------|
| SQL Server 2025 Express | 17.0.1000.7 | Database engine with native `VECTOR(512)` type |
| Entity Framework Core | 9.0.13 | ORM with LINQ-to-SQL |
| EFCore.SqlServer.VectorSearch | 9.0.0 | Maps `float[]` to `VECTOR(512)`, translates `EF.Functions.VectorDistance()` |
| Microsoft.Data.SqlClient | (transitive) | SQL Server ADO.NET driver |

### 3.5 Configuration & DI

| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.Extensions.DependencyInjection | 9.0.13 | IoC container |
| Microsoft.Extensions.Configuration.Json | 9.0.13 | appsettings.json reader |

---

## 4. AI/ML Models

### 4.1 SCRFD -- Face Detection

| Property | Value |
|----------|-------|
| **Full name** | Sample and Computation Redistribution for Efficient Face Detection |
| **Architecture** | Single-stage anchor-free CNN detector |
| **Source** | Bundled in FaceAiSharp.Bundle 0.5.23 NuGet package |
| **Input** | RGB image (any resolution) |
| **Output** | Per-face: bounding box (RectangleF), confidence (0.0-1.0), 5 landmark points |
| **Landmarks** | Left eye center, right eye center, nose tip, left mouth corner, right mouth corner |
| **Inference** | 20-50ms per frame on CPU |
| **Min confidence** | 0.7 (configurable) |

**Why SCRFD?** Faster and more accurate than MTCNN or RetinaFace for real-time desktop use. Single-stage design avoids the cascaded refinement overhead of MTCNN's 3-network pipeline.

### 4.2 ArcFace -- Face Embedding

| Property | Value |
|----------|-------|
| **Full name** | Additive Angular Margin Loss for Deep Face Recognition |
| **Architecture** | ResNet-based backbone (lightweight variant) |
| **Source** | Bundled in FaceAiSharp.Bundle 0.5.23 NuGet package |
| **Input** | Aligned face crop, 112x112 RGB (aligned using 5-point landmarks) |
| **Output** | 512-dimensional L2-normalized float vector |
| **Inference** | 50-100ms per face on CPU |
| **Accuracy** | 99.77% on LFW (Labeled Faces in the Wild) benchmark |

**How face alignment works:**
1. SCRFD provides 5 landmark coordinates (eyes, nose, mouth corners)
2. An affine transformation aligns the face to a canonical pose
3. The aligned face is cropped and resized to 112x112 pixels
4. This standardized input ensures consistent embeddings regardless of head pose

**How comparison works:**
- ArcFace outputs a 512-dim unit vector (L2 norm = 1.0)
- Cosine distance between two vectors measures dissimilarity:
  - `0.00` = identical faces
  - `0.35` = high-confidence same person
  - `0.55` = threshold boundary (match/no-match cutoff)
  - `1.00` = completely different faces
- Distance is computed inside SQL Server via `VECTOR_DISTANCE('cosine', a, b)`

**Storage:** 512 floats x 4 bytes = **2,048 bytes per embedding**. Stored natively as SQL Server `VECTOR(512)` column type.

### 4.3 Eye State Detector -- Blink Detection

| Property | Value |
|----------|-------|
| **Source** | Part of FaceAiSharp.Bundle (IEyeStateDetector) |
| **Input** | Cropped eye region (derived from 5-point landmarks) |
| **Output** | `EyeState.Open` or `EyeState.Closed` per eye |
| **Inference** | 2-5ms per frame |
| **Usage** | Tracks blink patterns for liveness verification |

**Eye region extraction:** FaceAiSharp's `ImageCalculations.GetEyeBoxesFromCenterPoints()` computes crop rectangles from the 5-point landmark eye centers. These boxes can extend outside image bounds when a face is near the edge -- the system clamps them with `ClampToImageBounds()` to prevent `ArgumentOutOfRangeException`.

### 4.4 MiniFASNetV2 -- Anti-Spoofing

| Property | Value |
|----------|-------|
| **Full name** | Minimal Face Anti-Spoofing Network V2 |
| **Origin** | Silent-Face-Anti-Spoofing project |
| **Source file** | `src/FaceRecApp.Core/Models/MiniFASNetV2.onnx` (1.7 MB) |
| **Input tensor** | `[1, 3, 80, 80]` -- batch=1, channels=3 (BGR), height=80, width=80 |
| **Input format** | Float32, pixel values 0-255 (no normalization) |
| **Output tensor** | `[1, 3]` -- 3 class logits |
| **Classification** | Softmax -> class 1 = Real face |
| **Threshold** | 0.5 confidence for "Real" classification |
| **Inference** | 5-20ms per face on CPU |

**Preprocessing pipeline:**
1. Expand face bounding box by **2.7x** (centered) to capture surrounding context (hair, background, screen edges)
2. Clamp expanded box to image bounds
3. Crop from original BGR frame
4. Resize to 80x80
5. Convert BGR HWC `byte[80,80,3]` to CHW `float[1,3,80,80]` (no normalization -- raw pixel values)
6. Run ONNX inference with single-thread configuration

**What each spoofing attack looks like to the model:**

| Attack | Visual cues the model detects |
|--------|-------------------------------|
| Phone screen (OLED/LCD) | Pixel grid pattern, moire artifacts, screen edge, backlight uniformity |
| Tablet/monitor | Larger screen texture, bezel edges, ambient light reflections |
| Printed photo | Paper texture, printing dots, color banding, flat lighting |
| Video replay | Screen texture even when face moves naturally |

---

## 5. Processing Pipeline

### 5.1 High-Level Flow

```
Webcam (30 fps)
    |
    v
CameraService.FrameCaptured event
    |
    +---[EVERY frame]---> DrawOverlays() ---> Display Buffer ---> WriteableBitmap
    |
    +---[Every 6th frame]---> Task.Run on Thread Pool
                                    |
                                    v
                    RecognitionPipeline.ProcessFrameAsync(Mat frame)
                                    |
                    +---------------+---------------+
                    |               |               |
                    v               v               v
            1. SCRFD Detect   2. ArcFace Embed  3. SQL Vector Search
            (20-50ms)         (50-100ms/face)   (5-75ms)
                    |               |               |
                    +---------------+---------------+
                                    |
                    4. MiniFASNetV2 Anti-Spoof (per face, 5-20ms)
                                    |
                    5. Liveness Check (primary face only)
                       - Blink detection
                       - Blink periodicity
                       - Micro-movement analysis
                       - Texture analysis
                                    |
                    6. Fire-and-forget: Log to RecognitionLogs
                                    |
                                    v
                    ResultsUpdated event ---> MainViewModel ---> UI
```

### 5.2 Step-by-Step Breakdown

**Step 0 -- Frame Quality Gate**
```
ImageConverter.IsFrameUsable(frame)
  - Brightness check: mean pixel value in [40, 220]
  - Blur check: Laplacian variance >= 50
  - Skips unusable frames (too dark, too bright, too blurry)
```

**Step 1 -- Face Detection (SCRFD)**
```
FaceDetectionService.DetectFaces(image)
  Input:  Image<Rgb24> (converted from Mat via JPEG codec, ~8ms)
  Output: List of FaceDetectorResult
          - Box: RectangleF (x, y, width, height)
          - Confidence: float (0.0 - 1.0)
          - Landmarks: 5 points (eyes, nose, mouth corners)
  Time:   20-50ms
```

**Step 2 -- Face Embedding (ArcFace)**
```
FaceRecognitionService.GenerateEmbedding(image, face)
  Input:  Full image + detected face with landmarks
  Process: Align face using landmarks -> crop 112x112 -> run ArcFace
  Output: float[512] (L2-normalized unit vector)
  Time:   50-100ms per face
  Validation: IsValidEmbedding() checks dims=512, non-zero, L2 norm in [0.9, 1.1]
```

**Step 3 -- Vector Search (SQL Server)**
```
FaceRepository.FindClosestMatchAsync(embedding)
  Path A (DiskANN index exists):
    VECTOR_SEARCH TVF -> approximate nearest neighbor -> ~5ms at 100K
  Path B (no index / fallback):
    ORDER BY VECTOR_DISTANCE('cosine', ...) -> brute-force scan -> ~75ms at 100K
  Output: FaceMatchResult { Person, Distance, IsMatch, Similarity }
  Match threshold: Distance <= 0.55
```

**Step 4 -- ML Anti-Spoofing (MiniFASNetV2)**
```
AntiSpoofService.Predict(frame, faceBox)
  Input:  Original BGR Mat + face bounding box
  Process: Expand box 2.7x -> crop -> resize 80x80 -> ONNX inference
  Output: SpoofPrediction { IsReal (bool), Confidence (float) }
  Time:   5-20ms per face
  Applied: Every detected face independently
```

**Step 5 -- Liveness Verification (Primary Face Only)**
```
LivenessService.ProcessFrame(image, face, embedding, frame)
  Only runs for the largest detected face (prevents phone-photo interference)
  Four checks evaluated simultaneously:
    1. BlinkCount >= 2 (natural eye blinks)
    2. HasNaturalBlinkTiming() (coefficient of variation > 0.15)
    3. HasNaturalMicroMovement() (position stddev >= 1.5px)
    4. HasNaturalTexture() (Laplacian + color + specular)
  All four must pass -> state transitions from Pending to Confirmed
```

**Step 6 -- Audit Logging (Fire-and-Forget)**
```
Task.Run(() => repository.LogRecognitionAsync(...))
  Writes: RecognitionLog entry (PersonId, Distance, WasRecognized, PassedLiveness)
  Updates: Person.LastSeenAt and Person.TotalRecognitions
  Non-blocking: Pipeline does not wait for DB write
```

### 5.3 Frame Skip Strategy

The camera runs at **30 fps**, but AI processing takes **120-250ms per frame**. To maintain smooth video display while running AI:

```
Frame 1:  Display only (overlays from previous results)
Frame 2:  Display only
Frame 3:  Display only
Frame 4:  Display only
Frame 5:  Display only
Frame 6:  Display + ProcessFrameAsync() dispatched to thread pool
Frame 7:  Display only (previous results still drawn)
...
```

This yields **~5 fps AI processing** while maintaining **30 fps smooth camera display**. The `ShouldProcess` flag is computed as `FrameNumber % ProcessEveryNFrames == 0` where `ProcessEveryNFrames = 6`.

An atomic `Interlocked.CompareExchange` flag prevents overlapping pipeline runs on slow machines -- if frame 12 arrives while frame 6 is still processing, frame 12 is skipped.

---

## 6. Liveness & Anti-Spoofing System

### 6.1 Defense-in-Depth Architecture

The system uses **5 independent anti-spoofing layers**. An attacker must defeat ALL layers simultaneously:

```
Layer 1: MiniFASNetV2 Neural Network (per-face, instant)
    |
    | Checks: Is this a real face or a screen/printout?
    | Result: SPOOF immediately if confidence < 0.5
    |
Layer 2: Blink Detection (stateful, requires ~4-6 seconds)
    |
    | Checks: Did the person blink naturally at least twice?
    | Defeats: Still photos (no blinks)
    |
Layer 3: Blink Periodicity (stateful, requires 3+ blinks)
    |
    | Checks: Are blink intervals irregular (natural) or regular (video loop)?
    | Defeats: Looped video with blinks
    |
Layer 4: Micro-Movement (stateful, requires ~4 seconds)
    |
    | Checks: Does the face sway naturally (>= 1.5px stddev)?
    | Defeats: Photos and screens held still
    |
Layer 5: Texture Analysis (stateful, requires 5 consecutive failures)
    |
    | Checks: Laplacian sharpness, color variation, specular highlights
    | Defeats: Screens (flat texture, uniform color, bright hotspots)
    |
    v
ALL PASS -> Liveness = CONFIRMED (expires after 30 seconds)
```

### 6.2 Liveness State Machine

```
             +---- identity change (dist > 0.40) ----+
             |                                         |
             v                                         |
        +---------+                             +-----------+
   +--->| PENDING |---all 4 checks pass-------->| CONFIRMED |
   |    +---------+                             +-----------+
   |         ^                                         |
   |         |                                         |
   |         +--- no face for 3 frames ----------------+
   |         +--- expired (30 seconds) ----------------+
   |         +--- manual reset (button) ---------------+
   |
   +--- app startup
```

### 6.3 Identity Tracking

To prevent person-swap attacks (person A blinks, swaps with person B who gets confirmed), the system tracks face identity using embedding distance:

- **During Pending:** The first detected embedding is stored as `_trackingEmbedding`. If a subsequent frame's embedding has distance > 0.40 from the tracking embedding, all blink counts reset.
- **After Confirmation:** The confirmed embedding is stored as `_lastConfirmedEmbedding`. Any frame with distance > 0.40 reverts to Pending and resets all state.

### 6.4 Blink Detection Algorithm

```
For each processed frame (~5 fps):
  1. Crop left eye and right eye regions from landmarks
  2. Classify each eye: Open or Closed (via IEyeStateDetector ONNX model)
  3. Combined state:
     - Both Open -> EyeState.Open
     - Both Closed -> EyeState.Closed
     - One open, one closed -> EyeState.Open (wink not counted as blink)
  4. Track duration:
     - Count consecutive Closed frames
     - On transition Closed -> Open:
       - If duration in [1, 3] frames (~200-600ms): valid blink
       - If duration > 3 frames: too long (sustained close, not a blink)
       - If duration < 1 frame: too fast (noise)
  5. Periodicity check (after 3+ blinks):
     - Compute time intervals between consecutive blinks
     - Calculate coefficient of variation (CV = stddev / mean)
     - CV > 0.15: natural (irregular) timing -> pass
     - CV <= 0.15: suspiciously regular -> possible video loop
```

### 6.5 Texture Analysis Algorithm

```
For each processed frame with a detected face:
  1. Crop face region from BGR Mat
  2. Convert to grayscale

  3. Laplacian Variance (high-frequency detail):
     - Apply Laplacian filter (second derivative of intensity)
     - Compute variance of output
     - Real skin: rich micro-texture -> variance >= 120
     - Screens: pixel resampling smooths texture -> lower variance
     - Printouts: printing artifacts differ from real texture

  4. Color Variation (channel uniformity):
     - Compute stddev of each BGR channel
     - Average the three stddevs
     - Real faces: uneven skin tone, shadows -> stddev >= 18.0
     - Phone screens: uniform LCD/OLED backlight -> lower stddev

  5. Specular Highlights (screen reflection):
     - Find max brightness pixel in grayscale
     - Compute ratio: max / mean
     - Real faces: ratio ~1.5-2.5 (natural lighting)
     - Phone screens: bright hotspot from ambient reflection -> ratio >= 3.0

  Failure tracking:
     - Must fail 5 CONSECUTIVE frames before flagging as spoof
     - Prevents false positives from momentary lighting changes
```

### 6.6 Attack Coverage Matrix

| Attack Type | Layer 1 (ML) | Layer 2 (Blink) | Layer 3 (Periodicity) | Layer 4 (Movement) | Layer 5 (Texture) |
|:-----------:|:---:|:---:|:---:|:---:|:---:|
| Still photo on phone | X | X | -- | X | X |
| Still photo printed | X | X | -- | X | X |
| Video with natural blinks | X | -- | X | -- | X |
| Video loop (repeated blinks) | X | -- | X | -- | -- |
| Person swap mid-session | -- | -- | -- | -- | -- |

Person swap is caught by **identity tracking** (embedding distance > 0.40 triggers full state reset).

---

## 7. Database & Vector Search

### 7.1 Schema

```sql
CREATE TABLE Persons (
    Id              INT PRIMARY KEY IDENTITY,
    Name            NVARCHAR(100) NOT NULL,
    Notes           NVARCHAR(500) NULL,
    ExternalId      NVARCHAR(50) NULL,           -- For external system linking
    CreatedAt       DATETIME2 NOT NULL,
    LastSeenAt      DATETIME2 NOT NULL,
    TotalRecognitions INT NOT NULL DEFAULT 0,
    IsActive        BIT NOT NULL DEFAULT 1       -- Soft delete
);

CREATE TABLE FaceEmbeddings (
    Id              INT PRIMARY KEY IDENTITY,
    PersonId        INT NOT NULL FOREIGN KEY REFERENCES Persons(Id) ON DELETE CASCADE,
    Embedding       VECTOR(512) NOT NULL,         -- SQL Server 2025 native type
    FaceThumbnail   VARBINARY(MAX) NULL,          -- JPEG thumbnail (112x112, ~5-10 KB)
    CaptureAngle    NVARCHAR(20) NULL,            -- "front", "left", "right"
    QualityScore    REAL NULL,
    CapturedAt      DATETIME2 NOT NULL
);

CREATE TABLE RecognitionLogs (
    Id              INT PRIMARY KEY IDENTITY,
    PersonId        INT NULL FOREIGN KEY REFERENCES Persons(Id) ON DELETE SET NULL,
    Distance        REAL NOT NULL,                -- Cosine distance (0.0 - 1.0)
    WasRecognized   BIT NOT NULL,
    PassedLiveness  BIT NOT NULL,
    StationId       NVARCHAR(50) NULL,
    Timestamp       DATETIME2 NOT NULL
);
```

### 7.2 EF Core VECTOR(512) Mapping

The `EFCore.SqlServer.VectorSearch` plugin maps C# `float[]` to SQL Server's native `VECTOR(512)` type:

```csharp
// Entity configuration (FaceDbContext.cs)
entity.Property(e => e.Embedding).HasColumnType("vector(512)");

// Query translation (generates VECTOR_DISTANCE in SQL)
var match = await db.FaceEmbeddings
    .Select(e => new {
        Embedding = e,
        Distance = EF.Functions.VectorDistance("cosine", e.Embedding, queryVector)
    })
    .OrderBy(x => x.Distance)
    .FirstOrDefaultAsync();
```

This plugin becomes unnecessary when EF Core 10 ships native `SqlVector<float>` support (.NET 10, November 2026).

### 7.3 Vector Search -- Two Execution Paths

The repository auto-detects the DiskANN index at startup and selects the optimal path:

**Path A: DiskANN Approximate Nearest Neighbor (~5ms at 100K)**
```sql
DECLARE @qv VECTOR(512) = CAST(@p0 AS VECTOR(512));
SELECT t.Id, t.PersonId, s.distance AS Distance
FROM VECTOR_SEARCH(
    TABLE = dbo.FaceEmbeddings AS t,
    COLUMN = Embedding,
    SIMILAR_TO = @qv,
    METRIC = 'cosine',
    TOP_N = 10
) AS s
ORDER BY s.distance;
```
- Uses DiskANN graph traversal -- O(log n) search
- 2,118 logical page reads (vs 33,460 for brute-force)
- Requires: `CREATE VECTOR INDEX ... WITH (METRIC='cosine', TYPE='diskann')`
- **Limitation:** Makes the FaceEmbeddings table read-only

**Path B: Brute-Force Exact KNN (~75ms at 100K)**
```sql
SELECT TOP(1) e.Id, e.PersonId,
    VECTOR_DISTANCE('cosine', e.Embedding, @p0) AS Distance
FROM FaceEmbeddings e
JOIN Persons p ON e.PersonId = p.Id
WHERE p.IsActive = 1
ORDER BY Distance ASC;
```
- Full table scan -- O(n) search
- 33,460 logical page reads
- No special index required
- Table remains fully writable

**Auto-detection logic (startup):**
```csharp
SELECT COUNT(*) FROM sys.indexes i
JOIN sys.tables t ON i.object_id = t.object_id
WHERE t.name = 'FaceEmbeddings' AND i.type_desc = 'VECTOR' AND i.is_disabled = 0
```

### 7.4 Storage Math

| Item | Size | Scale Example |
|------|------|---------------|
| One embedding | 2,048 bytes | -- |
| One thumbnail | ~7 KB | JPEG 112x112 |
| 1 person x 3 samples | ~27 KB | -- |
| 10,000 persons x 5 samples | ~450 MB | Small clinic |
| 100,000 persons x 1 sample | ~880 MB | Large facility |
| SQL Server Express limit | 10 GB (data) | Supports ~500K embeddings |

### 7.5 What Gets Stored Per Registration

| Data | Stored | Column | Size |
|------|--------|--------|------|
| 512-dim face vector | Yes | `Embedding VECTOR(512)` | 2,048 bytes |
| Cropped face JPEG (112x112) | Yes | `FaceThumbnail VARBINARY(MAX)` | ~5-10 KB |
| Capture angle label | Yes | `CaptureAngle NVARCHAR(20)` | ~20 bytes |
| Full camera frame | No | -- | -- |
| Original uncropped image | No | -- | -- |

---

## 8. User Interface

### 8.1 Design System

The UI uses a **warm neutral color palette** with no visual noise:

| Token | Hex | Usage |
|-------|-----|-------|
| BgPrimary | #F5F3F0 | Window background |
| BgSurface | #FFFFFF | Card/panel backgrounds |
| BgSurfaceDim | #EDEAE6 | Muted surface (hover) |
| BgDark | #2D2926 | Top bar, dark panels |
| TextPrimary | #2D2926 | Main text |
| TextSecondary | #78716C | Secondary text |
| TextMuted | #A8A29E | Hints, captions |
| AccentPrimary | #5B7F62 | Sage green (positive actions, LIVE badge) |
| AccentDanger | #B85C56 | Terracotta (danger, SPOOF badge) |
| AccentAmber | #C49A52 | Gold (warning, VERIFYING badge) |

### 8.2 Windows

**MainWindow** -- Camera feed + recognition panel
- Left: Live camera feed (640x480) with bounding box overlays
- Right: Detected faces list with name, similarity %, status badge
- Bottom: Pipeline timing (Detect/Embed/Search/Total ms), activity log
- Top bar: Camera toggle, Register, Database, Benchmark buttons + stats

**RegisterWindow** -- Single-capture registration
- Camera preview with face detection
- Name and notes input fields
- Duplicate detection (warns if face already registered)
- Auto-closes on successful registration

**DatabaseWindow** -- Person management
- DataGrid with all registered persons
- Stats panel: total persons, embeddings, recognitions, success rate
- Delete functionality (hard delete with cascade)

**BenchmarkWindow** -- Performance testing
- Run benchmark: 20 iterations of vector search + stats query + insert
- Populate synthetic data: 100 or 1,000 persons
- Cleanup: remove synthetic data
- Results display with Min/Max/Avg/Median/P95

### 8.3 Recognition Status Badges

| Badge | Color | Meaning |
|-------|-------|---------|
| LIVE | #5B7F62 (sage green) | Recognized + liveness confirmed |
| VERIFYING | #C49A52 (amber) | Recognized but liveness pending |
| MATCH | #5B7F62 (sage green) | Recognized + liveness confirmed (lower confidence) |
| SPOOF | #B85C56 (terracotta) | ML anti-spoofing detected a fake face |
| UNKNOWN | #A8A29E (stone) | Face not recognized in database |

### 8.4 Overlay Colors (Camera Feed)

| Color | BGR Value | Meaning |
|-------|-----------|---------|
| Green | (0, 200, 0) | Recognized, high confidence (distance <= 0.35) |
| Yellow | (0, 220, 220) | Recognized, standard confidence (0.35 < distance <= 0.55) |
| Orange | (0, 165, 255) | Unknown face or liveness not yet confirmed |
| Red | (0, 0, 255) | Spoof detected (red X drawn across face) |

---

## 9. Threading & Concurrency Model

### 9.1 Thread Architecture

```
[Camera Thread]              [Thread Pool]                [UI Thread]
(dedicated, AboveNormal)     (Task.Run)                   (WPF Dispatcher)
       |                          |                            |
   VideoCapture.Read()     ProcessFrameAsync()        CompositionTarget.Rendering
   ~30 fps                 ~5 fps (every 6th frame)   ~60 fps
       |                          |                            |
   FrameCaptured event      ONNX inference             Read display buffer
       |                    SQL queries                Update WriteableBitmap
       |                          |                    Update UI bindings
       v                          v                            |
   _latestDisplayFrame     ResultsUpdated event ----BeginInvoke--> UI update
   (locked write)          (C# event)
```

### 9.2 Producer-Consumer Display Pattern

The camera thread and UI thread are decoupled through a shared frame buffer:

```csharp
// Producer (camera thread, 30fps):
lock (_displayLock) {
    _latestDisplayFrame?.Dispose();
    _latestDisplayFrame = e.Frame;  // Transfer ownership
}

// Consumer (UI thread, ~60fps via CompositionTarget.Rendering):
lock (_displayLock) {
    frame = _latestDisplayFrame;
    _latestDisplayFrame = null;  // Take ownership
}
// Update WriteableBitmap in-place (Lock -> memcpy -> AddDirtyRect -> Unlock)
```

**Why this pattern?**
- No per-frame BitmapSource allocation (previous approach: 30 allocs/sec, ~27 MB/sec GC pressure)
- Camera thread never blocks on UI thread
- Intermediate frames are silently dropped (latest-wins)
- WriteableBitmap updated in-place (~1-2ms for 640x480)

### 9.3 Atomic Processing Guard

```csharp
// Prevents overlapping pipeline runs on slow machines
if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
    return _lastResults;  // Skip this frame, return cached results
try {
    // ... full pipeline processing ...
} finally {
    Interlocked.Exchange(ref _isProcessing, 0);
}
```

### 9.4 Error Isolation

Overlay drawing is wrapped in its own try/catch inside the frame handler:

```csharp
// Overlay failure MUST NOT prevent frame display
try { _pipeline.DrawOverlays(e.Frame); }
catch (Exception ex) { Debug.WriteLine($"Overlay error: {ex.Message}"); }

// Frame ALWAYS reaches the display buffer
lock (_displayLock) {
    _latestDisplayFrame = e.Frame;
}
```

---

## 10. Performance Benchmarks

### 10.1 Per-Operation Timing

| Operation | Typical Time | Notes |
|-----------|-------------|-------|
| Camera capture | ~33ms (30fps) | Background thread |
| Mat -> ImageSharp conversion | 8-10ms | JPEG encode/decode |
| Face detection (SCRFD) | 20-50ms | Single frame, any face count |
| Face embedding (ArcFace) | 50-100ms | Per face |
| Vector search (brute-force) | ~75ms | 100K embeddings, warm cache |
| Vector search (DiskANN) | ~5ms | 100K embeddings, warm cache |
| Anti-spoof (MiniFASNetV2) | 5-20ms | Per face |
| Liveness (blink + texture) | 5-10ms | Primary face only |
| WriteableBitmap update | 1-2ms | 640x480 in-place copy |
| DB log insert | 5-10ms | Fire-and-forget |

### 10.2 End-to-End Pipeline Timing

| Scenario | Time | Rate |
|----------|------|------|
| Single face, DiskANN | ~120ms | ~8 fps potential |
| Single face, brute-force | ~190ms | ~5 fps potential |
| Two faces, brute-force | ~300ms | ~3 fps potential |
| Display-only frames | <2ms | 30 fps maintained |

### 10.3 Vector Search at Scale (Warm Cache)

| Embeddings | Brute-Force | DiskANN | Speedup | Logical Reads |
|-----------|-------------|---------|---------|---------------|
| 100 | ~1ms | N/A | -- | -- |
| 1,000 | ~3ms | N/A | -- | -- |
| 10,000 | ~10ms | ~3ms | 3x | -- |
| 100,000 | ~75ms | ~5ms | 15x | 33K vs 2K |

### 10.4 Memory Footprint

| Component | Memory |
|-----------|--------|
| SCRFD ONNX model | ~10 MB |
| ArcFace ONNX model | ~30 MB |
| Eye State ONNX model | ~2 MB |
| MiniFASNetV2 ONNX model | ~1.7 MB |
| **Total AI models** | **~44 MB** |
| Per camera frame (640x480 BGR) | ~900 KB |
| EF Core DbContext | ~50 KB |
| App baseline (WPF) | ~80 MB |
| **Typical working set** | **~150-180 MB** |

---

## 11. Configuration & Thresholds

### 11.1 Recognition Thresholds

| Constant | Value | Meaning |
|----------|-------|---------|
| `DistanceThreshold` | 0.55 | Maximum cosine distance for a match (similarity >= 45%) |
| `HighConfidenceDistance` | 0.35 | High-confidence match (similarity >= 65%) |
| `EmbeddingDimensions` | 512 | ArcFace output vector size |

### 11.2 Camera Settings

| Constant | Value | Meaning |
|----------|-------|---------|
| `ProcessEveryNFrames` | 6 | AI runs on every 6th frame (30fps / 6 = 5fps AI) |
| `CameraWidth` | 640 | Capture resolution width |
| `CameraHeight` | 480 | Capture resolution height |

### 11.3 Liveness Parameters

| Constant | Value | Meaning |
|----------|-------|---------|
| `BlinkHistorySize` | 15 | Eye state queue (~3 seconds at 5fps) |
| `MinBlinksRequired` | 2 | Minimum natural blinks for confirmation |
| `LivenessExpirySeconds` | 30.0 | Confirmation TTL before reset |
| `IdentityChangeDistance` | 0.40 | Person-swap detection threshold |
| `MicroMovementHistorySize` | 20 | Face position queue (~4 seconds at 5fps) |
| `MinMicroMovementStdDev` | 1.5 | Minimum natural sway in pixels |
| `MinBlinkDurationFrames` | 1 | Shortest valid blink (~200ms) |
| `MaxBlinkDurationFrames` | 3 | Longest valid blink (~600ms) |

### 11.4 Texture Analysis Parameters

| Constant | Value | Meaning |
|----------|-------|---------|
| `MinLaplacianVariance` | 120.0 | Minimum texture sharpness (real skin) |
| `MinColorVariation` | 18.0 | Minimum color channel stddev |
| `MaxSpecularRatio` | 3.0 | Maximum brightness-to-mean ratio |
| `EnableTextureAnalysis` | true | Master toggle for texture checks |
| `TextureFailFramesRequired` | 5 | Consecutive failures before flagging |
| `NoFaceResetFrames` | 3 | No-face frames before full state reset |

### 11.5 Anti-Spoofing Parameters

| Constant | Value | Meaning |
|----------|-------|---------|
| `AntiSpoofThreshold` | 0.5 | MiniFASNetV2 confidence cutoff |
| `CropScale` | 2.7 | Face box expansion for context capture |
| `InputSize` | 80 | Model input resolution (80x80) |

---

## 12. Testing

### 12.1 Test Suite

| Test File | Type | Tests | Database |
|-----------|------|-------|----------|
| SimilarityTests.cs | Unit | 12 | None |
| EntityTests.cs | Unit | 5 | None |
| DatabaseTests.cs | Integration | 8 | InMemory or SQL Server |
| BulkPopulateTests.cs | Scale | 1 | SQL Server (real) |

### 12.2 Key Test Cases

**SimilarityTests** -- Pure math, no external dependencies:
- Identical vectors produce cosine similarity of 1.0
- Orthogonal vectors produce cosine similarity of 0.0
- Dimension mismatch throws exception
- Valid embedding has 512 dims and L2 norm near 1.0
- Threshold constants are within expected ranges

**EntityTests** -- Entity defaults and validation:
- Person defaults: IsActive=true, TotalRecognitions=0
- FaceEmbedding defaults: empty array, null thumbnail
- RecognitionLog similarity = 1 - distance

**DatabaseTests** -- EF Core CRUD:
- Register person with single/multiple embeddings
- Add face samples to existing person
- Soft delete (IsActive filtering)
- Hard delete with cascade
- Recognition logging
- Stats computation

**BulkPopulateTests** -- Scale validation:
- Insert 100,000 synthetic persons with embeddings
- Batch insert performance (~500/batch)
- Verified in 77.8 seconds

### 12.3 Running Tests

```bash
# All tests
dotnet test tests/FaceRecApp.Tests

# Specific suite
dotnet test tests/FaceRecApp.Tests --filter "FullyQualifiedName~SimilarityTests"

# Scale test (requires SQL Server)
dotnet test tests/FaceRecApp.Tests --filter "FullyQualifiedName~BulkPopulateTests"
```

---

## 13. Known Limitations

### 13.1 DiskANN Vector Index

- **Read-only table:** Creating a DiskANN index on FaceEmbeddings makes the table read-only. New face registrations require dropping the index first.
- **`ALLOW_STALE_VECTOR_INDEX`** is only available in Azure SQL Database, not SQL Server 2025 on-premises.
- **Index rebuild:** Must drop and recreate to include new data (no incremental updates).
- **Preview feature:** Requires `ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON`.

### 13.2 Image Conversion Overhead

- Mat -> ImageSharp conversion uses JPEG encode/decode (~8-10ms per frame)
- A direct pixel-format bridge would be faster but couples the image libraries

### 13.3 OpenCV ASCII-Only Text

- `Cv2.PutText()` only supports ASCII characters (code points < 128)
- Non-ASCII in person names causes `ArgumentException` at the P/Invoke boundary
- Mitigated by `SanitizeForOpenCv()` which replaces non-ASCII with '?'

### 13.4 Single Camera

- Currently supports one webcam (index 0)
- Multi-camera would require multiple CameraService instances

### 13.5 CPU-Only Inference

- All ONNX models run on CPU (no GPU acceleration configured)
- Adding CUDA/DirectML would require `Microsoft.ML.OnnxRuntime.Gpu` package

### 13.6 SQL Server Express Edition Limits

- 10 GB maximum database size
- 1 GB maximum memory usage
- 4 CPU cores maximum

---

## 14. Future Considerations

### 14.1 EF Core 10 Native Vector Support

When .NET 10 ships (November 2026), EF Core 10 will include native `SqlVector<float>` support. The `EFCore.SqlServer.VectorSearch` plugin can then be removed, and `float[]` properties can be replaced with `SqlVector<float>`.

### 14.2 GPU Acceleration

Adding `Microsoft.ML.OnnxRuntime.Gpu` (CUDA) or `Microsoft.ML.OnnxRuntime.DirectML` (DirectX) would significantly reduce inference times:
- Face detection: 20-50ms -> ~5ms
- Face embedding: 50-100ms -> ~10ms
- Anti-spoofing: 5-20ms -> ~2ms

### 14.3 Multi-Camera Support

Extend CameraService to manage multiple webcam instances, each feeding its own RecognitionPipeline instance with shared AI model singletons.

### 14.4 Face Quality Scoring

Add per-frame face quality assessment (blur, occlusion, pose angle) to:
- Skip low-quality frames during registration
- Weight embeddings by quality during search
- Alert users to adjust camera position

### 14.5 DiskANN Writable Tables

When SQL Server 2025 GA or a future update supports `ALLOW_STALE_VECTOR_INDEX` on-premises, the DiskANN index can remain active during writes, removing the need to drop/recreate it for new registrations.
