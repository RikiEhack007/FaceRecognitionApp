# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Offline desktop face recognition app using C# (.NET 9), WPF, FaceAiSharp (SCRFD + ArcFace), and SQL Server 2025 native `VECTOR(512)` search. Three projects in `FaceRecognitionApp.sln`:

- **FaceRecApp.Core** — Platform-agnostic business logic (AI services, repository, entities, helpers)
- **FaceRecApp.WPF** — Windows desktop UI (MVVM with CommunityToolkit.Mvvm)
- **FaceRecApp.Tests** — xUnit tests (unit + integration)

## Build & Run Commands

```bash
dotnet restore
dotnet build
dotnet run --project src/FaceRecApp.WPF/FaceRecApp.WPF.csproj
dotnet test tests/FaceRecApp.Tests
dotnet test tests/FaceRecApp.Tests --filter "FullyQualifiedName~SimilarityTests"
```

**Database migrations** (requires SQL Server 2025 Express running):
```bash
dotnet ef migrations add <Name> -p src/FaceRecApp.Core -s src/FaceRecApp.WPF
dotnet ef database update -p src/FaceRecApp.Core -s src/FaceRecApp.WPF
```

**Publish self-contained:**
```bash
dotnet publish src/FaceRecApp.WPF/FaceRecApp.WPF.csproj -c Release -r win-x64 --self-contained -o dist/FaceRecognitionApp
```

## Architecture

### Two-layer design

**Core** has no Windows dependencies and can be reused with other UI frameworks. **WPF** depends on Core and adds Windows-specific UI (BitmapSource, Dispatcher marshalling).

### Processing pipeline

`RecognitionPipeline` orchestrates per-frame processing: camera captures at 30fps, every 6th frame runs through detection → embedding → vector search → liveness check. Results fire events consumed by `MainViewModel`.

```
CameraService (30fps Mat) → RecognitionPipeline.ProcessFrameAsync()
  1. ImageConverter.MatToImageSharp()
  2. FaceDetectionService.DetectFaces() — SCRFD
  3. FaceRecognitionService.GenerateEmbedding() — ArcFace → float[512]
  4. FaceRepository.FindClosestMatchAsync() — SQL VECTOR_DISTANCE('cosine', ...)
  5. LivenessService.ProcessFrame() — blink detection
  6. RecognitionLog saved (fire-and-forget)
```

### Key services (all in `src/FaceRecApp.Core/Services/`)

| Service | Role |
|---|---|
| `FaceDetectionService` | SCRFD face detector → bounding boxes + 5-point landmarks |
| `FaceRecognitionService` | ArcFace → 512-dim float[] embedding |
| `LivenessService` | Eye state tracking for blink-based liveness |
| `CameraService` | OpenCvSharp webcam capture loop |
| `FaceRepository` | EF Core + SQL Server vector search CRUD |
| `RecognitionPipeline` | Orchestrator wiring all services together |
| `BenchmarkService` | Synthetic data generation + vector search perf testing |

### DI & threading

Services registered in `App.xaml.cs`. ONNX model services are **singletons** (expensive to load). `FaceRepository` is **transient** (fresh DbContext per operation). Camera runs on a background thread; UI updates marshal to WPF Dispatcher.

### Entities (`src/FaceRecApp.Core/Entities/`)

- `Person` — registered individual (Name, ExternalId, Notes, IsActive soft-delete)
- `FaceEmbedding` — 512-dim vector stored as SQL Server `VECTOR(512)`, linked to Person
- `RecognitionLog` — audit trail (distance, liveness result, timestamp)
- `RecognitionSettings` — configuration constants and thresholds

### Database

EF Core 9 with `EFCore.SqlServer.VectorSearch` preview plugin for `VECTOR(512)` mapping. The plugin becomes unnecessary when EF Core 10 ships native `SqlVector<float>` support (.NET 10, Nov 2026). Connection string in `src/FaceRecApp.WPF/appsettings.json`, defaults to `localhost\SQLEXPRESS` with Windows Auth.

## Conventions

- **MVVM**: `[ObservableProperty]` and `[RelayCommand]` source generators from CommunityToolkit.Mvvm
- **Async everywhere**: all services use `async Task`, no blocking DB calls
- **Namespaces**: `FaceRecApp.Core.{Services|Entities|Data|Helpers}`, `FaceRecApp.WPF.{ViewModels|Views|Converters|Helpers}`
- **Disposal**: all services implement `IDisposable`; ONNX models and camera cleaned up in `Dispose()`
- **Image types**: FaceAiSharp operates on `Image<Rgb24>` (ImageSharp); camera produces `OpenCvSharp.Mat`; `ImageConverter` bridges the two via JPEG encode/decode
- **Test naming**: `MethodName_Condition_ExpectedResult`
- **Nullable reference types** and **implicit usings** enabled project-wide

## Configuration

`src/FaceRecApp.WPF/appsettings.json` controls recognition thresholds (`DistanceThreshold: 0.55`, `HighConfidenceDistance: 0.35`), frame processing rate (`ProcessEveryNFrames: 6`), and camera settings. SQL scripts in `scripts/` provide DiskANN index optimizations for 5,000+ face scale.
