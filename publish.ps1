# ============================================================
# Face Recognition PoC — Build & Publish Script
# ============================================================
# Usage: Right-click → Run with PowerShell
#   or:  powershell -ExecutionPolicy Bypass -File publish.ps1
# ============================================================

param(
    [switch]$SkipBuild,
    [switch]$CreateZip,
    [string]$OutputDir = ".\dist"
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Face Recognition PoC — Build & Publish" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# ── Check prerequisites ──
Write-Host "[1/5] Checking prerequisites..." -ForegroundColor Yellow

$dotnetVersion = dotnet --version 2>$null
if (-not $dotnetVersion) {
    Write-Host "  ❌ .NET SDK not found. Install from https://dotnet.microsoft.com/download" -ForegroundColor Red
    exit 1
}
Write-Host "  ✅ .NET SDK: $dotnetVersion" -ForegroundColor Green

# ── Restore NuGet packages ──
Write-Host "[2/5] Restoring NuGet packages..." -ForegroundColor Yellow
dotnet restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ❌ NuGet restore failed" -ForegroundColor Red
    exit 1
}
Write-Host "  ✅ Packages restored" -ForegroundColor Green

# ── Build ──
if (-not $SkipBuild) {
    Write-Host "[3/5] Building solution (Release)..." -ForegroundColor Yellow
    dotnet build -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ❌ Build failed" -ForegroundColor Red
        exit 1
    }
    Write-Host "  ✅ Build successful" -ForegroundColor Green
} else {
    Write-Host "[3/5] Build skipped (--SkipBuild)" -ForegroundColor Gray
}

# ── Run tests ──
Write-Host "[4/5] Running unit tests..." -ForegroundColor Yellow
dotnet test tests/FaceRecApp.Tests/FaceRecApp.Tests.csproj -c Release --no-build --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ⚠️ Some tests failed (continuing with publish)" -ForegroundColor Yellow
} else {
    Write-Host "  ✅ All tests passed" -ForegroundColor Green
}

# ── Publish ──
Write-Host "[5/5] Publishing self-contained app..." -ForegroundColor Yellow

$publishDir = Join-Path $OutputDir "FaceRecognitionApp"
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish src/FaceRecApp.WPF/FaceRecApp.WPF.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "  ❌ Publish failed" -ForegroundColor Red
    exit 1
}

# Copy additional files
Copy-Item "scripts" -Destination $publishDir -Recurse -ErrorAction SilentlyContinue
Copy-Item "README.md" -Destination $publishDir -ErrorAction SilentlyContinue

# Check output size
$size = (Get-ChildItem $publishDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host "  ✅ Published to: $publishDir ($([math]::Round($size, 1)) MB)" -ForegroundColor Green

# ── Optional: Create ZIP ──
if ($CreateZip) {
    $zipFile = Join-Path $OutputDir "FaceRecognitionApp-v1.0.zip"
    if (Test-Path $zipFile) { Remove-Item $zipFile }
    
    Compress-Archive -Path $publishDir -DestinationPath $zipFile
    $zipSize = (Get-Item $zipFile).Length / 1MB
    Write-Host "  ✅ ZIP created: $zipFile ($([math]::Round($zipSize, 1)) MB)" -ForegroundColor Green
}

Write-Host ""
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  ✅ DONE!" -ForegroundColor Green
Write-Host "" 
Write-Host "  To run the app:" -ForegroundColor White
Write-Host "    1. Ensure SQL Server 2025 Express is running" -ForegroundColor Gray
Write-Host "    2. Run: $publishDir\FaceRecApp.WPF.exe" -ForegroundColor Gray
Write-Host ""
Write-Host "  First run will automatically create the database." -ForegroundColor Gray
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
