# Face Recognition PoC

Offline face recognition desktop application using **C#**, **FaceAiSharp** (ArcFace), and **SQL Server 2025** native vector search.

Detects, recognizes, and registers faces in real-time from a webcam, storing 512-dimensional face embeddings as `VECTOR(512)` in SQL Server and matching via `VECTOR_DISTANCE('cosine', ...)`.

---

## Prerequisites

| Requirement | Version | Link |
|---|---|---|
| Visual Studio | 2022 v17.12+ | [Download](https://visualstudio.microsoft.com/) |
| .NET SDK | 9.0+ | [Download](https://dotnet.microsoft.com/download) |
| SQL Server 2025 Express | v17 (GA) | [Download](https://www.microsoft.com/sql-server/sql-server-downloads) |
| SSMS | 22+ | [Download](https://aka.ms/ssms) |
| Webcam | Any USB or built-in | — |

---

## Quick Start

### 1. Install SQL Server 2025 Express
Run the installer, choose **Basic** install. Note instance name (default: `SQLEXPRESS`).

### 2. Open Solution
```bash
# Open in Visual Studio
start FaceRecognitionApp.sln
```
NuGet packages restore automatically.

### 3. Create Database
```bash
dotnet ef migrations add InitialCreate -p src/FaceRecApp.Core -s src/FaceRecApp.WPF
dotnet ef database update -p src/FaceRecApp.Core -s src/FaceRecApp.WPF
```

### 4. Verify (Optional)
Open SSMS → connect to `localhost\SQLEXPRESS` → run `scripts/01_verify_setup.sql`

### 5. Run
Set **FaceRecApp.WPF** as startup project → **F5**.

---

## How to Use

1. **Start Camera** — click to begin the webcam feed
2. **Register Person** — enter a name, face the camera, click "Capture & Register"
3. **Recognition** — registered faces are automatically identified with name + similarity %
4. **Database** — view/manage all registered persons
5. **Benchmark** — test vector search performance, populate synthetic data for scale testing

---

## Tech Stack

| Component | Library | License | Role |
|---|---|---|---|
| Face Detection | FaceAiSharp (SCRFD) | MIT | Detect faces + 5-point landmarks |
| Face Recognition | FaceAiSharp (ArcFace) | MIT | Generate 512-dim embeddings |
| Liveness | FaceAiSharp (Eye State) | MIT | Blink detection |
| Database | SQL Server 2025 Express | Free | Store embeddings as VECTOR(512) |
| Vector Search | Native VECTOR_DISTANCE() | Built-in | Cosine similarity in T-SQL |
| ORM | EF Core 9 + VectorSearch plugin | MIT | C# ↔ SQL mapping |
| Webcam | OpenCvSharp4 | BSD-3 | Camera capture + overlay drawing |
| UI | WPF (.NET 9) | Free | Desktop interface |
| Image Processing | SixLabors.ImageSharp | Apache 2.0 | Cross-platform image handling |
| MVVM | CommunityToolkit.Mvvm | MIT | Data binding, commands |
| AI Runtime | ONNX Runtime | MIT | Run neural network models on CPU |

**Total cost: $0**

---

## Project Structure

```
FaceRecognitionApp/
├── FaceRecognitionApp.sln
├── README.md
├── publish.ps1                          # Build + publish script
├── start.bat                            # Quick launcher
│
├── scripts/
│   ├── 01_verify_setup.sql             # Verify SQL Server 2025 vectors
│   ├── 02_advanced_optimizations.sql   # DiskANN index + stored procedures
│   └── 03_backup_maintenance.sql       # Backup + index rebuild + cleanup
│
├── src/
│   ├── FaceRecApp.Core/                # Business logic (no Windows deps)
│   │   ├── Entities/
│   │   │   ├── Person.cs               # Registered individual
│   │   │   ├── FaceEmbedding.cs        # 512-dim vector stored as VECTOR(512)
│   │   │   ├── RecognitionLog.cs       # Audit trail
│   │   │   └── RecognitionSettings.cs  # Thresholds + constants
│   │   ├── Data/
│   │   │   ├── FaceDbContext.cs         # EF Core context + vector mapping
│   │   │   └── FaceDbContextFactory.cs  # Design-time factory for migrations
│   │   ├── Services/
│   │   │   ├── FaceDetectionService.cs  # SCRFD face detector
│   │   │   ├── FaceRecognitionService.cs # ArcFace embedding generator
│   │   │   ├── LivenessService.cs       # Blink detection
│   │   │   ├── CameraService.cs         # Webcam capture loop
│   │   │   ├── FaceRepository.cs        # SQL vector search + CRUD
│   │   │   ├── RecognitionPipeline.cs   # Orchestrates everything
│   │   │   ├── RecognitionResult.cs     # Result DTO
│   │   │   └── BenchmarkService.cs      # Performance testing
│   │   └── Helpers/
│   │       ├── ImageConverter.cs        # Mat ↔ ImageSharp
│   │       └── OverlayRenderer.cs       # Draw face boxes + labels
│   │
│   └── FaceRecApp.WPF/                # Desktop UI
│       ├── ViewModels/
│       │   └── MainViewModel.cs         # MVVM: camera, pipeline, bindings
│       ├── Views/
│       │   ├── MainWindow.xaml/.cs      # Live feed + results + log
│       │   ├── RegisterWindow.xaml/.cs  # Person registration dialog
│       │   ├── DatabaseWindow.xaml/.cs  # Person management + stats
│       │   └── BenchmarkWindow.xaml/.cs # Performance testing UI
│       ├── Converters/Converters.cs     # XAML value converters
│       ├── Helpers/WpfImageHelper.cs    # BitmapSource conversions
│       ├── App.xaml/.cs                 # DI container + DB init
│       └── appsettings.json             # Connection string + config
│
└── tests/
    └── FaceRecApp.Tests/
        ├── EntityTests.cs               # Entity unit tests
        ├── SimilarityTests.cs           # Cosine math tests
        └── DatabaseTests.cs             # CRUD integration tests
```

---

## Data Flow

```
Camera (30fps) → FrameCaptured event
                      │
                Every 6th frame?
                ├── No  → Draw last overlays → Display
                └── Yes → RecognitionPipeline.ProcessFrameAsync()
                              │
                      1. ImageConverter.MatToImageSharp()
                      2. FaceDetectionService → boxes + landmarks
                      3. FaceRecognitionService → float[512]
                      4. FaceRepository.FindClosestMatchAsync()
                         │
                         └─ SQL Server:
                            SELECT TOP(1) *,
                              VECTOR_DISTANCE('cosine', Embedding, @query)
                            FROM FaceEmbeddings
                            JOIN Persons
                            ORDER BY distance ASC
                         │
                      5. LivenessService → blink check
                      6. Return RecognitionResult → UI
```

---

## Configuration

Edit `appsettings.json`:

```jsonc
{
  "ConnectionStrings": {
    // Change server name if your instance is different
    "FaceRecognitionDb": "Server=localhost\\SQLEXPRESS;Database=FaceRecognitionDb;..."
  },
  "Recognition": {
    "DistanceThreshold": 0.55,    // Max distance for match (lower = stricter)
    "HighConfidenceDistance": 0.35, // Green box threshold
    "ProcessEveryNFrames": 6,      // AI runs at 30/6 = 5fps
    "CameraIndex": 0,             // 0 = default webcam
    "CameraWidth": 640,
    "CameraHeight": 480
  }
}
```

---

## Publishing

### Self-contained .exe (no .NET install required on target)

```powershell
# PowerShell
.\publish.ps1 -CreateZip

# Or manually:
dotnet publish src/FaceRecApp.WPF/FaceRecApp.WPF.csproj -c Release -r win-x64 --self-contained -o dist/FaceRecognitionApp
```

Output: `dist/FaceRecognitionApp/` (~200-300 MB, includes .NET runtime + ONNX models)

### Deploy
1. Copy the published folder to target machine
2. Install SQL Server 2025 Express on target
3. Run `FaceRecApp.WPF.exe` (auto-creates database on first run)

---

## SQL Scripts

| Script | Purpose | When to Run |
|---|---|---|
| `01_verify_setup.sql` | Verify VECTOR(512) works | After initial setup |
| `02_advanced_optimizations.sql` | DiskANN index + stored procedures + views | When scaling to 5,000+ faces |
| `03_backup_maintenance.sql` | Backup, index rebuild, log cleanup | Weekly/monthly maintenance |

---

## Upgrade Path (PoC → Production)

| Component | PoC (Free) | Production |
|---|---|---|
| Face Model | FaceAiSharp (ArcFace light) | NEC NeoFace / InsightFace R100 |
| Liveness | Blink detection (webcam) | 3D IR camera + iBeta PAD |
| Database | SQL Server 2025 Express (50GB, 1GB RAM) | SQL Server Standard/Enterprise |
| Scale | ~1,000 faces | 500,000+ faces |
| Camera | USB webcam | Intel RealSense D435 |
| Compliance | None | HIPAA / PDPA encryption |
