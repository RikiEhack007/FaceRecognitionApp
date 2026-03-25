using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FaceRecApp.Core.Entities;
using FaceRecApp.Core.Services;
using FaceRecApp.WPF.Helpers;
using FaceRecApp.WPF.Services;
using Microsoft.Extensions.DependencyInjection;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FaceRecApp.WPF.ViewModels;

/// <summary>
/// Main view model — split into partial classes:
///   MainViewModel.cs         — fields, constructor, helpers, Dispose
///   MainViewModel.Camera.cs  — camera control, frame handling, pipeline results
///   MainViewModel.Identify.cs — identify, searches, navigation commands
///   MainViewModel.Verify.cs  — verify (1:1), photo update
///   MainViewModel.Enrol.cs   — enrol step commands + form properties
///   MainViewModel.Visit.cs   — visit logging, workflow helpers, theme toggle
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly CameraService _camera;
    private readonly RecognitionPipeline _pipeline;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;
    private CancellationTokenSource? _preWarmCts;
    private volatile bool _isSwitching;

    private WriteableBitmap? _writeableBitmap;
    private Mat? _latestDisplayFrame;
    private readonly object _displayLock = new();

    // ── Observable Properties ──

    [ObservableProperty] private BitmapSource? _cameraFrame;
    [ObservableProperty] private bool _isCameraRunning;
    [ObservableProperty] private string _statusText = "Ready. Click 'Start Camera' to begin.";
    [ObservableProperty] private string _fpsText = "FPS: --";
    [ObservableProperty] private string _timingText = "";
    [ObservableProperty] private string _livenessText = "";
    [ObservableProperty] private string _databaseText = "Database: 0 persons";
    [ObservableProperty] private ObservableCollection<RecognitionResultViewModel> _currentResults = new();
    [ObservableProperty] private ObservableCollection<string> _activityLog = new();
    [ObservableProperty] private ObservableCollection<CameraDeviceInfo> _cameraDevices = new();
    [ObservableProperty] private CameraDeviceInfo? _selectedCamera;

    // ── Clinical Workflow State ──
    // Step 1 = Identify, Step 2 = Verify, Step 3 = Enrol, Step 4 = Visit

    [ObservableProperty] private int _currentWorkflowStep = 1;
    [ObservableProperty] private bool _isEnrolStepRequired;

    private Patient? _selectedPatient;
    [ObservableProperty] private string _identificationMethod = "";
    [ObservableProperty] private bool _needsBiometricUpdate;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isFingerprintListening;
    private Dictionary<int, string> _fingerprintTemplateMap = new();

    // ── Step 1: Identify ──
    [ObservableProperty] private string _identifyResultHeader = "";
    [ObservableProperty] private string _identifyResultColor = "#A8A29E";
    [ObservableProperty] private string _identifyResultText = "";
    [ObservableProperty] private bool _hasIdentifyResult;
    [ObservableProperty] private string _manualSearchQuery = "";
    [ObservableProperty] private ObservableCollection<PatientSummary> _manualSearchResults = new();
    [ObservableProperty] private bool _hasManualSearchResults;
    [ObservableProperty] private bool _showEnrolNewOption;
    [ObservableProperty] private bool _showNoMatchMessage;

    // ── Patient Card ──
    [ObservableProperty] private bool _showPatientCard;
    [ObservableProperty] private string _patientPid = "";
    [ObservableProperty] private string _patientName = "";
    [ObservableProperty] private string _patientSex = "";
    [ObservableProperty] private string _patientAge = "";
    [ObservableProperty] private string _patientIdentifyTiming = "";
    [ObservableProperty] private string _patientInitials = "";
    [ObservableProperty] private string _patientDob = "";

    // ── Step 2: Verify ──
    [ObservableProperty] private string _verifyResultHeader = "";
    [ObservableProperty] private string _verifyResultColor = "#A8A29E";
    [ObservableProperty] private string _verifyResultText = "";
    [ObservableProperty] private bool _hasVerifyResult;
    [ObservableProperty] private bool _facialChangeChecked;
    [ObservableProperty] private string _facialChangeReason = "";
    [ObservableProperty] private string _photoUpdateStatus = "";
    [ObservableProperty] private bool _idCardVerified;

    // ── Step 4: Visit ──
    [ObservableProperty] private string _visitServiceType = "";
    [ObservableProperty] private string _visitChiefComplaint = "";
    [ObservableProperty] private bool _visitLogged;
    [ObservableProperty] private ObservableCollection<string> _serviceTypes = new(BiometricRemarks.ServiceTypes);

    // ── Theme ──
    [ObservableProperty] private bool _isDarkTheme;

    // ── Constructor ──

    public MainViewModel()
    {
        _camera = App.Services.GetRequiredService<CameraService>();
        _pipeline = App.Services.GetRequiredService<RecognitionPipeline>();
        _dispatcher = Dispatcher.CurrentDispatcher;

        _camera.FrameCaptured += OnFrameCaptured;
        _camera.CameraError += (_, msg) => AddLog($"Camera error: {msg}");
        CompositionTarget.Rendering += OnRender;
        _pipeline.ResultsUpdated += OnResultsUpdated;
        _pipeline.ProcessingError += (_, msg) => AddLog(msg);

        _ = RefreshDatabaseStatsAsync();
        _ = RefreshCameraDevicesAsync();
    }

    // ── Helpers ──

    private async Task RefreshDatabaseStatsAsync()
    {
        try
        {
            var repo = App.Services.GetRequiredService<FaceRepository>();
            var stats = await repo.GetStatsAsync();
            var searchMode = repo.UseVectorSearch ? "DiskANN" : "KNN";
            _dispatcher.Invoke(() => DatabaseText = $"DB: {stats.TotalPatients} persons, {stats.TotalEmbeddings} samples [{searchMode}]");
        }
        catch { }
    }

    private void AddLog(string message)
    {
        _dispatcher.BeginInvoke(() =>
        {
            ActivityLog.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
            while (ActivityLog.Count > 100) ActivityLog.RemoveAt(ActivityLog.Count - 1);
        });
    }

    private string? _lastLogKey;
    private void AddLogIfNew(string message, string? dedupeKey = null)
    {
        var key = dedupeKey ?? message;
        if (key == _lastLogKey) return;
        _lastLogKey = key;
        AddLog(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CompositionTarget.Rendering -= OnRender;
        _camera.FrameCaptured -= OnFrameCaptured;
        _preWarmCts?.Cancel();
        _preWarmCts?.Dispose();
        _camera.Stop();
        try { App.Services.GetRequiredService<FingerprintService>().FingerprintCaptured -= OnFingerprintForSearch; } catch { }
        lock (_displayLock) { _latestDisplayFrame?.Dispose(); _latestDisplayFrame = null; }
    }
}

public class RecognitionResultViewModel
{
    public string Name { get; }
    public string PID { get; }
    public string Similarity { get; }
    public string Status { get; }
    public string StatusColor { get; }
    public Patient? Patient { get; }

    public RecognitionResultViewModel(RecognitionResult result)
    {
        Patient = result.Patient;
        Name = result.Patient?.FullName ?? "Unknown";
        PID = result.Patient?.IDCard ?? "";
        Similarity = result.SimilarityText;

        if (result.IsSpoofDetected) { Status = "SPOOF"; StatusColor = "#B85C56"; }
        else if (result.IsHighConfidence) { Status = result.IsLive ? "LIVE" : "VERIFYING"; StatusColor = result.IsLive ? "#5B7F62" : "#C49A52"; }
        else if (result.IsRecognized) { Status = result.IsLive ? "MATCH" : "VERIFYING"; StatusColor = result.IsLive ? "#5B7F62" : "#C49A52"; }
        else { Status = "UNKNOWN"; StatusColor = "#A8A29E"; }
    }
}
