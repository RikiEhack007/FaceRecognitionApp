# Face Recognition System -- Architecture Documentation

## 1. System Overview

An offline desktop face recognition system built with C# (.NET 9) that performs real-time detection, identification, and liveness verification using commodity hardware (webcam + CPU). No cloud services, no GPU required.

```
+-------------------+     +-------------------+     +-------------------+
|  FaceRecApp.WPF   |     |  FaceRecApp.Core  |     |  SQL Server 2025  |
|  (Desktop UI)     |---->|  (Business Logic) |---->|  Express Edition  |
|                   |     |                   |     |                   |
|  MVVM + WPF       |     |  ONNX Models      |     |  VECTOR(512)      |
|  CommunityToolkit |     |  OpenCvSharp      |     |  DiskANN Index    |
|  WriteableBitmap  |     |  ImageSharp       |     |  EF Core 9        |
+-------------------+     +-------------------+     +-------------------+
```

**Key capabilities:**
- Real-time face detection and identification at ~5 fps AI processing
- 512-dimensional face embedding via ArcFace neural network
- SQL Server 2025 native vector search (cosine distance)
- 4-layer liveness verification (blink + periodicity + micro-movement + texture)
- ML-based anti-spoofing (MiniFASNetV2 neural network)
- DiskANN approximate nearest neighbor index (~5ms search at 100K faces)

---

## 2. Tech Stack

### 2.1 Runtime & Framework

| Component | Version | Role |
|-----------|---------|------|
| .NET | 9.0 | Runtime |
| WPF | .NET 9 Windows | Desktop UI framework |
| C# | 13 | Language |
| CommunityToolkit.Mvvm | 8.4.0 | MVVM source generators (`[ObservableProperty]`, `[RelayCommand]`) |

### 2.2 AI / Machine Learning

| Package | Version | Model | Role |
|---------|---------|-------|------|
| FaceAiSharp.Bundle | 0.5.23 | **SCRFD** (ONNX) | Face detection -- bounding boxes + 5-point landmarks |
| FaceAiSharp.Bundle | 0.5.23 | **ArcFace** (ONNX) | Face embedding -- produces 512-dim float vector |
| FaceAiSharp.Bundle | 0.5.23 | Eye State Detector (ONNX) | Blink detection -- Open / Closed classification |
| Microsoft.ML.OnnxRuntime | 1.20.1 | Runtime | ONNX model inference engine (CPU) |
| Custom | -- | **MiniFASNetV2** (ONNX, 1.7 MB) | Anti-spoofing -- real vs spoof (phone/printout/video) |

### 2.3 Image Processing

| Package | Version | Role |
|---------|---------|------|
| SixLabors.ImageSharp | 3.1.12 | Cross-platform image manipulation (FaceAiSharp's native format) |
| SixLabors.ImageSharp.Drawing | 2.1.7 | Image drawing operations |
| OpenCvSharp4 | 4.10.0 | Webcam capture, frame manipulation, overlay rendering |
| OpenCvSharp4.WpfExtensions | 4.10.0 | Mat to BitmapSource conversion |

### 2.4 Database

| Component | Version | Role |
|-----------|---------|------|
| SQL Server 2025 Express | 17.0.1000.7 | Database engine with native `VECTOR(512)` type |
| Microsoft.EntityFrameworkCore.SqlServer | 9.0.13 | ORM |
| EFCore.SqlServer.VectorSearch | 9.0.0 | Maps `float[]` to `VECTOR(512)`, translates `EF.Functions.VectorDistance()` |
| Microsoft.Data.SqlClient | (transitive) | SQL Server connectivity |

### 2.5 Configuration & DI

| Package | Version | Role |
|---------|---------|------|
| Microsoft.Extensions.DependencyInjection | 9.0.13 | IoC container |
| Microsoft.Extensions.Configuration.Json | 9.0.13 | `appsettings.json` reader |

---

## 3. Project Structure

```
FaceRecognitionApp.sln
|
+-- src/
|   +-- FaceRecApp.Core/                    # Platform-agnostic business logic
|   |   +-- Data/
|   |   |   +-- FaceDbContext.cs            # EF Core context (VECTOR(512) mapping)
|   |   |   +-- FaceDbContextFactory.cs     # Design-time factory for migrations
|   |   +-- Entities/
|   |   |   +-- Person.cs                  # Registered individual
|   |   |   +-- FaceEmbedding.cs           # 512-dim vector (maps to VECTOR(512))
|   |   |   +-- RecognitionLog.cs          # Audit trail
|   |   |   +-- RecognitionSettings.cs     # Static thresholds and constants
|   |   +-- Helpers/
|   |   |   +-- ImageConverter.cs          # Mat <-> ImageSharp conversion
|   |   |   +-- OverlayRenderer.cs         # Draw bounding boxes + labels on frames
|   |   +-- Models/
|   |   |   +-- MiniFASNetV2.onnx          # Anti-spoofing model (1.7 MB)
|   |   +-- Services/
|   |       +-- FaceDetectionService.cs    # SCRFD face detector
|   |       +-- FaceRecognitionService.cs  # ArcFace embedding generator
|   |       +-- LivenessService.cs         # Blink + micro-movement + texture
|   |       +-- AntiSpoofService.cs        # MiniFASNetV2 spoof classifier
|   |       +-- CameraService.cs           # Webcam capture loop
|   |       +-- FaceRepository.cs          # DB CRUD + vector search
|   |       +-- RecognitionPipeline.cs     # Orchestrator (ties everything together)
|   |       +-- RecognitionResult.cs       # Per-face result DTO
|   |       +-- BenchmarkService.cs        # Performance testing + synthetic data
|   |
|   +-- FaceRecApp.WPF/                    # Windows desktop UI
|       +-- App.xaml                       # Shared styles + color system
|       +-- App.xaml.cs                    # DI container + startup
|       +-- appsettings.json              # Configuration
|       +-- Converters/
|       |   +-- Converters.cs             # XAML value converters
|       +-- Helpers/
|       |   +-- WpfImageHelper.cs         # Mat <-> BitmapSource, WriteableBitmap
|       +-- ViewModels/
|       |   +-- MainViewModel.cs          # Main window logic (MVVM)
|       +-- Views/
|           +-- MainWindow.xaml/.cs        # Camera feed + results panel
|           +-- RegisterWindow.xaml/.cs    # Face registration dialog
|           +-- DatabaseWindow.xaml/.cs    # Person management
|           +-- BenchmarkWindow.xaml/.cs   # Performance testing UI
|
+-- tests/
|   +-- FaceRecApp.Tests/
|       +-- DatabaseTests.cs              # EF Core + vector search
|       +-- EntityTests.cs                # Entity validation
|       +-- SimilarityTests.cs            # Embedding distance metrics
|       +-- BulkPopulateTests.cs          # Synthetic 100K data generation
|
+-- scripts/
    +-- 01_create_database.sql
    +-- 02_advanced_optimizations.sql     # DiskANN index creation
```

---

## 4. ONNX Models Deep Dive

### 4.1 SCRFD -- Face Detection

| Property | Value |
|----------|-------|
| **Model** | SCRFD (Sample and Computation Redistribution for Efficient Face Detection) |
| **Source** | Bundled in FaceAiSharp.Bundle NuGet |
| **Input** | RGB image (any resolution) |
| **Output** | Per-face: bounding box (`RectangleF`), confidence score (0-1), 5 landmark points (left eye, right eye, nose, left mouth, right mouth) |
| **Speed** | 20-50ms per frame on CPU |
| **Purpose** | Finds all faces in a frame and their precise locations |

**Architecture:** Single-stage anchor-free detector. More efficient than MTCNN/RetinaFace for real-time use. Produces multi-scale feature maps and predicts boxes + landmarks at each scale.

### 4.2 ArcFace -- Face Embedding

| Property | Value |
|----------|-------|
| **Model** | ArcFace (Additive Angular Margin Loss for Deep Face Recognition) |
| **Source** | Bundled in FaceAiSharp.Bundle NuGet |
| **Input** | Aligned face crop (112x112 RGB, preprocessed using 5-point landmarks) |
| **Output** | 512-dimensional L2-normalized float vector |
| **Speed** | 50-100ms per face on CPU |
| **Purpose** | Converts a face image into a numerical "fingerprint" for comparison |

**How it works:**
1. The 5 landmark points from SCRFD are used to align the face (rotation, scale)
2. The aligned face is resized to 112x112
3. ArcFace produces a 512-dim vector where faces of the same person cluster together
4. Cosine distance between two vectors measures similarity:
   - `0.0` = identical
   - `< 0.35` = high-confidence same person
   - `< 0.55` = likely same person
   - `> 0.55` = different person

**Storage:** 512 floats x 4 bytes = 2,048 bytes per embedding. Stored as SQL Server `VECTOR(512)`.

### 4.3 Eye State Detector -- Blink Detection

| Property | Value |
|----------|-------|
| **Model** | Eye state classifier (part of FaceAiSharp) |
| **Source** | Bundled in FaceAiSharp.Bundle NuGet |
| **Input** | Cropped eye region (from 5-point landmarks) |
| **Output** | `EyeState.Open` or `EyeState.Closed` |
| **Speed** | 2-5ms per frame |
| **Purpose** | Tracks eye open/close for blink-based liveness verification |

**Blink detection logic:**
1. Track eye state over 15-frame history
2. A blink = Open -> Closed (1-3 frames) -> Open
3. Require 2+ natural blinks with irregular timing (CV > 0.15)
4. Too-regular blinking suggests a video replay attack

### 4.4 MiniFASNetV2 -- Anti-Spoofing

| Property | Value |
|----------|-------|
| **Model** | MiniFASNetV2 from Silent-Face-Anti-Spoofing |
| **Source** | Downloaded from [yakhyo/face-anti-spoofing](https://github.com/yakhyo/face-anti-spoofing/releases) |
| **File** | `src/FaceRecApp.Core/Models/MiniFASNetV2.onnx` (1.7 MB) |
| **Input** | `[1, 3, 80, 80]` -- NCHW, BGR, float32, pixel values 0-255 |
| **Output** | `[1, 3]` logits -> softmax -> class 1 = Real face |
| **Speed** | 5-20ms per face on CPU |
| **Purpose** | Classifies whether a face is real or a spoof (phone screen, printout, video) |

**Preprocessing pipeline:**
1. Expand face bounding box by 2.7x (centered) to capture surrounding context
2. Clamp to image bounds
3. Crop and resize to 80x80
4. Convert BGR HWC `byte[80,80,3]` to CHW `float[1,3,80,80]`
5. No normalization -- raw pixel values (0-255)

**What it detects:**

| Attack Type | Detection Method |
|-------------|-----------------|
| Phone photo (OLED/LCD) | Screen pixel patterns, moire, lighting artifacts |
| Video on phone | Screen texture even in motion |
| Printed photo | Paper texture, printing artifacts |

---

## 5. Processing Pipeline

### 5.1 Frame Processing Flow

```
Camera (30 fps)
    |
    v
FrameEventArgs { Frame, FrameNumber, ShouldProcess }
    |
    +--[Every frame]--> DrawOverlays() --> Display Buffer --> WriteableBitmap
    |
    +--[Every 6th frame]--> Task.Run (Thread Pool)
                                |
                                v
                    RecognitionPipeline.ProcessFrameAsync()
                                |
        +-----------------------+-----------------------+
        |                       |                       |
        v                       v                       v
   1. SCRFD Detect        2. ArcFace Embed       3. SQL Vector Search
   (20-50ms)              (50-100ms/face)        (5-75ms)
        |                       |                       |
        v                       v                       v
   Face boxes +           float[512] per         FaceMatchResult
   5 landmarks            detected face          { Person, Distance }
        |                       |                       |
        +-----------------------+-----------------------+
                                |
                    4. Per-face ML Anti-Spoof
                       MiniFASNetV2 (5-20ms/face)
                                |
                    5. Primary face: Liveness Check
                       Blink + Movement + Texture
                                |
                    6. Fire-and-forget: Log to DB
                                |
                                v
                    ResultsUpdated event --> MainViewModel --> UI
```

### 5.2 Per-Frame Timing Budget

At 6-frame skip on a 30fps camera, the pipeline runs at ~5 fps with a ~200ms budget per cycle.

```
Typical frame breakdown (1 face):
  Detection:     30ms
  Embedding:     70ms
  Vector Search:  5ms (DiskANN) or 75ms (brute-force)
  Anti-Spoof:    10ms
  Liveness:       5ms
  Logging:        async (non-blocking)
  ─────────────────
  Total:        ~120ms (DiskANN) or ~190ms (brute-force)
```

### 5.3 Thread Model

```
[Camera Thread]          [Thread Pool]              [UI Thread]
     |                        |                          |
  30fps capture        ProcessFrameAsync()       CompositionTarget.Rendering
     |                   (every 6th frame)            (~60fps)
     |                        |                          |
  Mat frame ----lock----> _latestDisplayFrame ----lock----> WriteableBitmap
     |                        |                          |
     |                   ONNX inference              Update display
     |                   SQL queries                 Update results
     |                        |                          |
     |                  ResultsUpdated ----BeginInvoke--> Update UI
```

**Key threading rules:**
- Camera thread NEVER blocks on UI thread
- ONNX inference runs on thread pool via `Task.Run`
- `Interlocked.CompareExchange` prevents overlapping pipeline runs
- UI updates use `Dispatcher.BeginInvoke` (non-blocking)
- `WriteableBitmap` updated in-place (Lock/memcpy/Unlock, ~1ms)

---

## 6. Database Architecture

### 6.1 Schema

```
+------------------+       +-------------------+       +-------------------+
|     Persons      |       |  FaceEmbeddings   |       | RecognitionLogs   |
+------------------+       +-------------------+       +-------------------+
| PK  Id (int)     |<------| FK  PersonId      |       | PK  Id (int)      |
|     Name (100)   |  1:N  | PK  Id (int)      |       | FK  PersonId (?)  |
|     Notes (500)  |       |     Embedding      |       |     Distance      |
|     ExternalId   |       |     VECTOR(512)    |       |     WasRecognized |
|     CreatedAt    |       |     FaceThumbnail  |       |     PassedLiveness|
|     LastSeenAt   |       |     CaptureAngle   |       |     StationId     |
|     TotalRecog.  |       |     QualityScore   |       |     Timestamp     |
|     IsActive     |       |     CapturedAt     |       +-------------------+
+------------------+       +-------------------+
                                    |
                           IX_Vector (DiskANN)
                           cosine metric
```

### 6.2 Vector Search -- Two Modes

**Mode 1: DiskANN (Approximate Nearest Neighbor)**
```sql
-- Uses the DiskANN graph index for O(log n) search
DECLARE @qv VECTOR(512) = CAST(@p0 AS VECTOR(512));
SELECT TOP(1) t.Id, t.PersonId, s.distance
FROM VECTOR_SEARCH(
    TABLE = dbo.FaceEmbeddings AS t,
    COLUMN = Embedding,
    SIMILAR_TO = @qv,
    METRIC = 'cosine',
    TOP_N = 10
) AS s;
-- ~5ms at 100K embeddings, 2,118 logical reads
```

**Mode 2: Brute-Force (Exact KNN)**
```sql
-- Scans every row, computes cosine distance for each
SELECT TOP(1) e.Id, e.PersonId,
    VECTOR_DISTANCE('cosine', e.Embedding, @p0) AS Distance
FROM FaceEmbeddings e
JOIN Persons p ON e.PersonId = p.Id
WHERE p.IsActive = 1
ORDER BY Distance ASC;
-- ~75ms at 100K embeddings, 33,460 logical reads
```

**Auto-detection:** At startup, `FaceRepository.DetectVectorIndexAsync()` checks `sys.indexes` for an enabled VECTOR index. If found, uses VECTOR_SEARCH TVF. Falls back to brute-force if index is absent or gets dropped.

**DiskANN limitation:** The table becomes **read-only** when a DiskANN index exists (SQL Server 2025 preview limitation). New face registrations require dropping the index first.

### 6.3 Performance at Scale

| Embeddings | Brute-Force (KNN) | DiskANN (ANN) | Logical Reads |
|------------|-------------------|---------------|---------------|
| 100 | ~1ms | N/A | -- |
| 1,000 | ~3ms | N/A | -- |
| 10,000 | ~10ms | ~3ms | -- |
| 100,000 | ~75ms | ~5ms | 33K vs 2K |
| 1,000,000 | ~750ms (est.) | ~10ms (est.) | -- |

---

## 7. Liveness & Anti-Spoofing System

### 7.1 Defense Layers

The system uses a defense-in-depth approach with 5 independent checks:

```
                    +------------------------------+
                    |     Per-Face (Instant)        |
                    |                              |
                    |  Layer 1: MiniFASNetV2 ONNX  |
                    |  Real vs Spoof classifier    |
                    |  (5-20ms per face)           |
                    +------------------------------+
                                 |
                          Pass? (conf >= 0.5)
                                 |
                    +------------------------------+
                    |     Primary Face (Stateful)   |
                    |                              |
                    |  Layer 2: Blink Detection     |
                    |  2+ natural blinks required  |
                    |                              |
                    |  Layer 3: Blink Periodicity   |
                    |  CV > 0.15 (irregular)       |
                    |                              |
                    |  Layer 4: Micro-Movement      |
                    |  StdDev >= 1.5px of position |
                    |                              |
                    |  Layer 5: Texture Analysis    |
                    |  Laplacian + color variation  |
                    +------------------------------+
                                 |
                          ALL pass?
                                 |
                    +------------------------------+
                    |   Liveness State: CONFIRMED   |
                    |   (expires after 30 seconds)  |
                    +------------------------------+
```

### 7.2 State Machine

```
                  +--- identity change (dist > 0.40) ---+
                  |                                      |
                  v                                      |
             +---------+                          +-----------+
             | PENDING |---all 4 checks pass----->| CONFIRMED |
             +---------+                          +-----------+
                  ^                                      |
                  |                                      |
                  +--- no face (3 frames) ---------------+
                  +--- expired (30 seconds) -------------+
```

### 7.3 What Each Layer Catches

| Attack | Layer 1 (ML) | Layer 2 (Blink) | Layer 3 (Periodicity) | Layer 4 (Movement) | Layer 5 (Texture) |
|--------|:---:|:---:|:---:|:---:|:---:|
| Phone photo (still) | X | X | -- | X | X |
| Phone video (replay) | X | -- | X | -- | X |
| Printed photo | X | X | -- | X | X |
| Video with blinks | X | -- | X | -- | -- |
| Person swap mid-session | -- | -- | -- | -- | -- |

Person swap is caught by **identity tracking** (embedding distance > 0.40 resets liveness state).

---

## 8. Core-WPF Boundary

### 8.1 Dependency Direction

```
FaceRecApp.WPF ────depends on────> FaceRecApp.Core
     (UI)                              (Logic)

Core has ZERO references to WPF.
Core uses: ImageSharp, OpenCvSharp, EF Core, ONNX Runtime
WPF uses:  Core + WPF-specific adapters (WriteableBitmap, Dispatcher)
```

### 8.2 Image Type Bridge

FaceAiSharp operates on `Image<Rgb24>` (ImageSharp). The camera produces `OpenCvSharp.Mat`. WPF displays `BitmapSource`.

```
Camera ──Mat──> Pipeline ──ImageConverter──> Image<Rgb24> ──> FaceAiSharp
                  |
                  Mat (with overlays drawn by OverlayRenderer)
                  |
          WpfImageHelper
                  |
                  v
           WriteableBitmap ──> WPF Image control
```

The conversion path: `Mat` -> JPEG encode -> `MemoryStream` -> JPEG decode -> `Image<Rgb24>`. This costs ~5-10ms but avoids direct pixel format coupling.

### 8.3 DI Container (App.xaml.cs)

```
Singletons (lifetime = app):
  FaceDetectionService      # SCRFD ONNX model (~15 MB in memory)
  FaceRecognitionService    # ArcFace ONNX model (~35 MB in memory)
  LivenessService           # Eye state ONNX model + state tracking
  AntiSpoofService          # MiniFASNetV2 ONNX model (~1.7 MB)
  CameraService             # Webcam handle
  RecognitionPipeline       # Wires all services together

Transient (new instance per request):
  FaceRepository            # Fresh DbContext per DB operation
  BenchmarkService          # Performance testing
```

### 8.4 Event Flow (Core -> WPF)

```
RecognitionPipeline.ResultsUpdated (C# event)
    |
    v
MainViewModel.OnResultsUpdated() [subscribed in constructor]
    |
    v
Dispatcher.BeginInvoke() [marshal to UI thread]
    |
    v
Update ObservableProperties --> WPF data binding --> UI refresh
```

No WPF types leak into Core. The pipeline fires plain C# events with `IReadOnlyList<RecognitionResult>`. The ViewModel translates to `RecognitionResultViewModel` (with WPF-specific color strings).

---

## 9. Data Flow Diagrams

### 9.1 Recognition Flow

```
[Webcam] --30fps--> [CameraService]
                        |
                   FrameCaptured event
                        |
                   [MainViewModel.OnFrameCaptured]
                        |
            +-----------+-----------+
            |                       |
      (every frame)          (every 6th frame)
            |                       |
    DrawOverlays(frame)     Task.Run { ProcessFrameAsync(frame) }
            |                       |
    Display buffer           [RecognitionPipeline]
            |                    |     |     |     |     |
    [OnRender]              Detect  Embed  Search  Spoof  Liveness
            |                    |     |     |     |     |
    WriteableBitmap          Results aggregated
            |                       |
        WPF Image            ResultsUpdated event
                                    |
                             [MainViewModel]
                                    |
                             UI data binding
```

### 9.2 Registration Flow

```
[RegisterWindow]
      |
  User enters name, faces camera
      |
  OnCaptureClick()
      |
  CameraService.CaptureSnapshot() --> Mat
      |
  RecognitionPipeline.RegisterFromFrameAsync(frame, name, notes)
      |
      +-- FaceDetectionService.DetectLargestFace(frame)
      |       --> FaceDetectorResult (box + landmarks)
      |
      +-- FaceRecognitionService.GenerateEmbedding(image, face)
      |       --> float[512]
      |
      +-- FaceRepository.RegisterPersonAsync(name, embedding, thumbnail, notes)
      |       --> INSERT Person + FaceEmbedding (VECTOR(512))
      |
  Success --> auto-close dialog
```

---

## 10. Configuration Reference

### appsettings.json

```json
{
  "ConnectionStrings": {
    "FaceRecognitionDb": "Server=localhost,60240;Database=FaceRecognitionDb;..."
  },
  "Recognition": {
    "DistanceThreshold": 0.55,        // Max cosine distance for a "match"
    "HighConfidenceDistance": 0.35,    // Green badge threshold
    "MinEnrollmentSamples": 1,        // Min face samples to register
    "ProcessEveryNFrames": 6,         // AI runs on every Nth frame
    "CameraIndex": 0,                 // Webcam device index
    "CameraWidth": 640,               // Capture resolution
    "CameraHeight": 480
  }
}
```

### RecognitionSettings.cs (compile-time constants)

| Constant | Value | Purpose |
|----------|-------|---------|
| `EmbeddingDimensions` | 512 | ArcFace output size |
| `DistanceThreshold` | 0.55 | Match/no-match cutoff |
| `HighConfidenceDistance` | 0.35 | High-confidence cutoff |
| `MinBlinksRequired` | 2 | Blinks for liveness |
| `LivenessExpirySeconds` | 30.0 | Liveness confirmation TTL |
| `IdentityChangeDistance` | 0.40 | Face swap detection |
| `MinLaplacianVariance` | 120.0 | Screen/print texture check |
| `AntiSpoofThreshold` | 0.5 | MiniFASNetV2 real/spoof cutoff |
| `ProcessEveryNFrames` | 6 | AI processing rate (30fps / 6 = 5fps) |

---

## 11. Build & Run

```bash
# Restore + Build
dotnet restore
dotnet build

# Run the desktop app
dotnet run --project src/FaceRecApp.WPF/FaceRecApp.WPF.csproj

# Run tests
dotnet test tests/FaceRecApp.Tests

# Database migrations
dotnet ef migrations add <Name> -p src/FaceRecApp.Core -s src/FaceRecApp.WPF
dotnet ef database update -p src/FaceRecApp.Core -s src/FaceRecApp.WPF

# Publish self-contained
dotnet publish src/FaceRecApp.WPF/FaceRecApp.WPF.csproj -c Release -r win-x64 --self-contained -o dist/
```

### Prerequisites
- .NET 9 SDK
- SQL Server 2025 Express (with `PREVIEW_FEATURES = ON` for DiskANN)
- USB webcam
- Windows 10/11 (WPF requirement)
