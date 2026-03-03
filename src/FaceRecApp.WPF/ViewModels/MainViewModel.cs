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
/// Main view model — drives the entire application.
/// 
/// Responsibilities:
///   - Start/stop camera
///   - Feed frames to RecognitionPipeline
///   - Update UI with recognition results
///   - Manage activity log
///   - Open registration / database windows
/// 
/// Uses CommunityToolkit.Mvvm for:
///   [ObservableProperty] → auto-generates property + INotifyPropertyChanged
///   [RelayCommand]       → auto-generates ICommand for button bindings
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly CameraService _camera;
    private readonly RecognitionPipeline _pipeline;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    // ─── Display pipeline (producer-consumer) ───
    // Camera thread writes latest frame to buffer; UI thread reads at render pace.
    // This decouples camera capture (30fps) from UI rendering (~60fps WPF).
    private WriteableBitmap? _writeableBitmap;
    private Mat? _latestDisplayFrame;
    private readonly object _displayLock = new();

    // ──────────────────────────────────────────────
    // Observable Properties (auto-generate PropertyChanged)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Current camera frame (with overlays drawn) for display.
    /// </summary>
    [ObservableProperty]
    private BitmapSource? _cameraFrame;

    /// <summary>
    /// Is the camera currently running?
    /// </summary>
    [ObservableProperty]
    private bool _isCameraRunning;

    /// <summary>
    /// Status bar text.
    /// </summary>
    [ObservableProperty]
    private string _statusText = "Ready. Click 'Start Camera' to begin.";

    /// <summary>
    /// Current FPS display.
    /// </summary>
    [ObservableProperty]
    private string _fpsText = "FPS: --";

    /// <summary>
    /// Pipeline timing display.
    /// </summary>
    [ObservableProperty]
    private string _timingText = "";

    /// <summary>
    /// Liveness status display.
    /// </summary>
    [ObservableProperty]
    private string _livenessText = "";

    /// <summary>
    /// Number of registered persons.
    /// </summary>
    [ObservableProperty]
    private string _databaseText = "Database: 0 persons";

    /// <summary>
    /// Current recognition results for display in the side panel.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<RecognitionResultViewModel> _currentResults = new();

    /// <summary>
    /// Activity log (recent events).
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<string> _activityLog = new();

    /// <summary>
    /// Available camera devices (populated by RefreshCameraDevices).
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<CameraDeviceInfo> _cameraDevices = new();

    /// <summary>
    /// Currently selected camera device.
    /// Changing this while the camera is running triggers a hot-switch.
    /// </summary>
    [ObservableProperty]
    private CameraDeviceInfo? _selectedCamera;

    // ─── Clinical Workflow State ───
    // 4-step workflow: Identify → Verify → Enrol → Visit

    /// <summary>Currently selected patient in the workflow.</summary>
    private Person? _selectedPatient;

    /// <summary>How the patient was found: "face" or "manual".</summary>
    [ObservableProperty]
    private string _identificationMethod = "";

    /// <summary>True when patient found via manual search (amber banner).</summary>
    [ObservableProperty]
    private bool _needsBiometricUpdate;

    /// <summary>True while an async workflow operation is running.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True when waiting for finger placement on scanner.</summary>
    [ObservableProperty]
    private bool _isFingerprintListening;

    /// <summary>Maps SDK cache FID (FingerprintTemplate.Id) → PersonId for resolving matches.</summary>
    private Dictionary<int, int> _fingerprintTemplateMap = new();

    // ─── Step 1: Identify ───

    [ObservableProperty]
    private string _identifyResultHeader = "";

    [ObservableProperty]
    private string _identifyResultColor = "#A8A29E";

    [ObservableProperty]
    private string _identifyResultText = "";

    [ObservableProperty]
    private bool _hasIdentifyResult;

    [ObservableProperty]
    private string _manualSearchQuery = "";

    [ObservableProperty]
    private ObservableCollection<PersonSummary> _manualSearchResults = new();

    [ObservableProperty]
    private bool _hasManualSearchResults;

    [ObservableProperty]
    private bool _showEnrolNewOption;

    // ─── Patient Card ───

    [ObservableProperty]
    private bool _showPatientCard;

    [ObservableProperty]
    private string _patientPid = "";

    [ObservableProperty]
    private string _patientName = "";

    [ObservableProperty]
    private string _patientSex = "";

    [ObservableProperty]
    private string _patientAge = "";

    [ObservableProperty]
    private string _patientIdentifyTiming = "";

    // ─── Step 2: Verify ───

    [ObservableProperty]
    private bool _showVerifySection;

    [ObservableProperty]
    private string _verifyResultHeader = "";

    [ObservableProperty]
    private string _verifyResultColor = "#A8A29E";

    [ObservableProperty]
    private string _verifyResultText = "";

    [ObservableProperty]
    private bool _hasVerifyResult;

    [ObservableProperty]
    private bool _facialChangeChecked;

    [ObservableProperty]
    private string _facialChangeReason = "";

    [ObservableProperty]
    private string _photoUpdateStatus = "";

    // ─── Step 4: Visit ───

    [ObservableProperty]
    private bool _showVisitSection;

    [ObservableProperty]
    private string _visitServiceType = "";

    [ObservableProperty]
    private string _visitChiefComplaint = "";

    [ObservableProperty]
    private bool _visitLogged;

    [ObservableProperty]
    private ObservableCollection<string> _serviceTypes = new(BiometricRemarks.ServiceTypes);

    // ──────────────────────────────────────────────
    // Constructor
    // ──────────────────────────────────────────────

    public MainViewModel()
    {
        _camera = App.Services.GetRequiredService<CameraService>();
        _pipeline = App.Services.GetRequiredService<RecognitionPipeline>();
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Subscribe to camera frames (producer: capture thread writes to buffer)
        _camera.FrameCaptured += OnFrameCaptured;
        _camera.CameraError += (_, msg) => AddLog($"Camera error: {msg}");

        // Subscribe to WPF render loop (consumer: UI thread reads from buffer)
        // CompositionTarget.Rendering fires once per WPF render frame (~60fps),
        // synced with the rendering pipeline for smooth display.
        CompositionTarget.Rendering += OnRender;

        // Subscribe to pipeline results
        _pipeline.ResultsUpdated += OnResultsUpdated;
        _pipeline.ProcessingError += (_, msg) => AddLog(msg);

        // Initial database stats
        _ = RefreshDatabaseStatsAsync();

        // Initial camera device scan
        RefreshCameraDevices();
    }

    /// <summary>
    /// Hot-switch: when the user picks a different camera while running,
    /// stop the current camera and start the newly selected one.
    /// </summary>
    partial void OnSelectedCameraChanged(CameraDeviceInfo? value)
    {
        if (!IsCameraRunning || value == null)
            return;

        _camera.Stop();
        bool success = _camera.Start(value);
        if (success)
        {
            _pipeline.SkipAntiSpoof = value.IsPhoneCamera;
            StatusText = $"Switched to {value.Name}";
            AddLog($"Switched to camera: {value.Name}");
            if (value.IsPhoneCamera)
                AddLog("Anti-spoof bypassed (virtual camera)");
        }
        else
        {
            IsCameraRunning = false;
            StatusText = $"Failed to open {value.Name}";
            AddLog($"Camera switch failed: {value.Name}");
        }
    }

    // ──────────────────────────────────────────────
    // Commands (bound to buttons via [RelayCommand])
    // ──────────────────────────────────────────────

    [RelayCommand]
    private void ToggleCamera()
    {
        if (IsCameraRunning)
        {
            _camera.Stop();
            _pipeline.SkipAntiSpoof = false;
            IsCameraRunning = false;
            StatusText = "Camera stopped.";
            FpsText = "FPS: --";
            AddLog("Camera stopped");
        }
        else
        {
            if (SelectedCamera == null)
            {
                StatusText = "No camera selected. Click Refresh to scan for devices.";
                AddLog("No camera selected");
                return;
            }

            bool success = _camera.Start(SelectedCamera);
            if (success)
            {
                IsCameraRunning = true;
                _pipeline.SkipAntiSpoof = SelectedCamera.IsPhoneCamera;
                StatusText = $"Camera running ({SelectedCamera.Name}) — detecting faces...";
                AddLog($"Camera started: {SelectedCamera.Name}");
                if (SelectedCamera.IsPhoneCamera)
                    AddLog("Anti-spoof bypassed (virtual camera)");
            }
            else
            {
                StatusText = $"Failed to open {SelectedCamera.Name}. Check connection.";
                AddLog("Camera failed to start");
            }
        }
    }

    [RelayCommand]
    private void RefreshCameraDevices()
    {
        var devices = _camera.GetAvailableDevices();

        CameraDevices.Clear();
        foreach (var device in devices)
            CameraDevices.Add(device);

        var selected = _camera.AutoSelectDevice(devices);
        SelectedCamera = selected;

        if (devices.Count == 0)
        {
            AddLog("No cameras found");
        }
        else
        {
            AddLog($"Found {devices.Count} camera(s)");
            if (selected != null)
                AddLog($"Auto-selected: {selected.Name}");
        }
    }

    [RelayCommand]
    private void OpenRegister()
    {
        var window = new Views.EnrolmentWindow();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();

        // Refresh stats after enrolment
        _ = RefreshDatabaseStatsAsync();
    }

    [RelayCommand]
    private void OpenDatabase()
    {
        var window = new Views.DatabaseWindow();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();

        // Refresh stats after changes
        _ = RefreshDatabaseStatsAsync();
    }

    [RelayCommand]
    private void OpenBenchmark()
    {
        var window = new Views.BenchmarkWindow();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();

        _ = RefreshDatabaseStatsAsync();
    }

    [RelayCommand]
    private void ResetLiveness()
    {
        _pipeline.ResetLiveness();
        LivenessText = "Liveness reset — waiting for blink...";
        AddLog("Liveness reset");
    }

    [RelayCommand]
    private void ClearLog()
    {
        _dispatcher.Invoke(() => ActivityLog.Clear());
    }

    // ──────────────────────────────────────────────
    // Clinical Workflow Commands
    // ──────────────────────────────────────────────

    // ─── Step 1: Identify (1:N) ───

    [RelayCommand]
    private async Task IdentifyAsync()
    {
        if (!IsCameraRunning)
        {
            StatusText = "Camera must be running to identify.";
            AddLog("Identify failed: camera not running");
            return;
        }

        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Identifying...";
        AddLog("Identify started");

        try
        {
            using var frame = _camera.CaptureSnapshot();
            if (frame == null)
            {
                StatusText = "Failed to capture frame.";
                AddLog("Identify failed: no frame");
                return;
            }

            var result = await Task.Run(() => _pipeline.IdentifyFromFrameAsync(frame));

            _dispatcher.Invoke(() =>
            {
                PatientIdentifyTiming = $"{result.Elapsed.TotalMilliseconds:F0}ms";

                if (!result.Success)
                {
                    IdentifyResultHeader = "ERROR";
                    IdentifyResultText = result.Error ?? "Identification failed.";
                    IdentifyResultColor = "#B85C56";
                    HasIdentifyResult = true;
                    ShowEnrolNewOption = false;
                    StatusText = result.Error ?? "Identification failed.";
                    AddLog($"Identify: {result.Error}");
                    return;
                }

                if (result.IsIdentified && result.Recognition?.Person != null)
                {
                    var person = result.Recognition.Person;

                    IdentifyResultHeader = result.Recognition.IsHighConfidence ? "IDENTIFIED (HIGH)" : "IDENTIFIED";
                    IdentifyResultColor = "#5B7F62";
                    IdentifyResultText = result.Recognition.SimilarityText;
                    HasIdentifyResult = true;
                    ShowEnrolNewOption = false;

                    // Populate patient card
                    SetSelectedPatient(person, "face");
                    NeedsBiometricUpdate = false;

                    StatusText = $"Identified: {person.FullName} ({result.Recognition.SimilarityText})";
                    AddLog($"Identified: {person.IDCard} {person.FullName} ({result.Recognition.SimilarityText})");
                }
                else
                {
                    _selectedPatient = null;

                    IdentifyResultHeader = "UNKNOWN";
                    IdentifyResultColor = "#C49A52";
                    IdentifyResultText = "Face not enrolled in the system.";
                    HasIdentifyResult = true;
                    ShowEnrolNewOption = true;
                    ShowPatientCard = false;

                    StatusText = "Face not recognized.";
                    AddLog($"Identify: unknown face ({result.Recognition?.SimilarityText ?? "N/A"})");
                }
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() =>
            {
                IdentifyResultHeader = "ERROR";
                IdentifyResultText = ex.Message;
                IdentifyResultColor = "#B85C56";
                HasIdentifyResult = true;
            });
            AddLog($"Identify error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ─── Manual Search ───

    [RelayCommand]
    private async Task ManualSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(ManualSearchQuery))
            return;

        IsBusy = true;
        StatusText = $"Searching for \"{ManualSearchQuery.Trim()}\"...";

        try
        {
            var repo = App.Services.GetRequiredService<FaceRepository>();
            var results = await repo.SearchPatientsByNameAsync(ManualSearchQuery.Trim());

            _dispatcher.Invoke(() =>
            {
                ManualSearchResults.Clear();
                foreach (var r in results)
                    ManualSearchResults.Add(r);

                HasManualSearchResults = results.Count > 0;
                StatusText = results.Count > 0
                    ? $"Found {results.Count} patient(s)"
                    : "No patients found matching that name.";
                AddLog($"Search: \"{ManualSearchQuery.Trim()}\" → {results.Count} result(s)");
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Search failed: {ex.Message}";
            AddLog($"Search error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SelectPatientAsync(PersonSummary summary)
    {
        if (summary == null) return;

        IsBusy = true;
        try
        {
            var repo = App.Services.GetRequiredService<FaceRepository>();
            var person = await repo.GetPatientByPidAsync(summary.IDCard);

            if (person == null)
            {
                StatusText = $"Patient {summary.IDCard} not found.";
                return;
            }

            _dispatcher.Invoke(() =>
            {
                SetSelectedPatient(person, "manual");
                NeedsBiometricUpdate = true;
                HasManualSearchResults = false;

                StatusText = $"Selected: {person.FullName} ({person.IDCard})";
                AddLog($"Selected (manual): {person.IDCard} {person.FullName}");
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load patient: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ─── Photo Search (Biometric Search — photo) ───

    [RelayCommand]
    private async Task PhotoSearchAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a patient photo",
            Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        StatusText = "Searching by photo...";
        AddLog($"Photo search: {System.IO.Path.GetFileName(dialog.FileName)}");

        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<Rgb24>(dialog.FileName);
            var result = await Task.Run(() => _pipeline.IdentifyFromImageAsync(image));

            _dispatcher.Invoke(() =>
            {
                PatientIdentifyTiming = $"{result.Elapsed.TotalMilliseconds:F0}ms";

                if (!result.Success)
                {
                    IdentifyResultHeader = "ERROR";
                    IdentifyResultText = result.Error ?? "Photo search failed.";
                    IdentifyResultColor = "#B85C56";
                    HasIdentifyResult = true;
                    ShowEnrolNewOption = false;
                    StatusText = result.Error ?? "Photo search failed.";
                    AddLog($"Photo search: {result.Error}");
                    return;
                }

                if (result.IsIdentified && result.Recognition?.Person != null)
                {
                    var person = result.Recognition.Person;
                    IdentifyResultHeader = "IDENTIFIED (PHOTO)";
                    IdentifyResultColor = "#5B7F62";
                    IdentifyResultText = result.Recognition.SimilarityText;
                    HasIdentifyResult = true;
                    ShowEnrolNewOption = false;

                    SetSelectedPatient(person, "photo");
                    NeedsBiometricUpdate = true;

                    StatusText = $"Identified from photo: {person.FullName}";
                    AddLog($"Photo match: {person.IDCard} {person.FullName} ({result.Recognition.SimilarityText})");
                }
                else
                {
                    _selectedPatient = null;
                    IdentifyResultHeader = "UNKNOWN";
                    IdentifyResultColor = "#C49A52";
                    IdentifyResultText = "No match found for this photo.";
                    HasIdentifyResult = true;
                    ShowEnrolNewOption = true;
                    ShowPatientCard = false;
                    StatusText = "Photo not recognized.";
                    AddLog("Photo search: no match");
                }
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() =>
            {
                IdentifyResultHeader = "ERROR";
                IdentifyResultText = ex.Message;
                IdentifyResultColor = "#B85C56";
                HasIdentifyResult = true;
            });
            AddLog($"Photo search error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ─── Fingerprint Search ───

    [RelayCommand]
    private async Task FingerprintSearchAsync()
    {
        var scanner = App.Services.GetRequiredService<FingerprintService>();

        if (!scanner.IsDeviceOpen)
        {
            // Try to init and open
            int devCount = scanner.Initialize();
            if (devCount < 0)
            {
                IdentifyResultHeader = "ERROR";
                IdentifyResultColor = "#B85C56";
                IdentifyResultText = "Fingerprint SDK failed to load. Ensure ZKFinger SDK is installed and libzkfp.dll is present.";
                HasIdentifyResult = true;
                StatusText = "Fingerprint SDK load failed.";
                AddLog("Fingerprint search: SDK load failed");
                return;
            }
            if (devCount == 0)
            {
                IdentifyResultHeader = "UNAVAILABLE";
                IdentifyResultColor = "#A8A29E";
                IdentifyResultText = "No fingerprint scanner connected. Use photo search or manual search instead.";
                HasIdentifyResult = true;
                StatusText = "Fingerprint scanner not found.";
                AddLog("Fingerprint search: no scanner detected");
                return;
            }

            if (!scanner.OpenDevice(0))
            {
                IdentifyResultHeader = "ERROR";
                IdentifyResultColor = "#B85C56";
                IdentifyResultText = "Failed to open fingerprint scanner.";
                HasIdentifyResult = true;
                StatusText = "Failed to open fingerprint scanner.";
                AddLog("Fingerprint search: failed to open device");
                return;
            }
        }

        // Load templates into cache if empty
        if (scanner.CacheCount == 0)
        {
            StatusText = "Loading fingerprint database...";
            var repo = App.Services.GetRequiredService<FaceRepository>();
            var templates = await repo.GetAllFingerprintTemplatesAsync();

            if (templates.Count == 0)
            {
                IdentifyResultHeader = "EMPTY";
                IdentifyResultColor = "#C49A52";
                IdentifyResultText = "No fingerprints enrolled in the system yet.";
                HasIdentifyResult = true;
                StatusText = "No fingerprint templates in database.";
                AddLog("Fingerprint search: database empty");
                return;
            }

            // Store mapping for resolving matches (FID → PersonId)
            _fingerprintTemplateMap = templates.ToDictionary(
                t => t.Key, t => t.Value.PersonId);

            // Load into SDK cache
            var cacheTemplates = templates.ToDictionary(
                t => t.Key, t => t.Value.Template);
            scanner.LoadTemplates(cacheTemplates);

            AddLog($"Loaded {templates.Count} fingerprint template(s) into cache");
        }

        // Subscribe for next capture (one-shot)
        IsFingerprintListening = true;
        StatusText = "Place finger on scanner...";
        AddLog("Fingerprint search: waiting for finger");

        scanner.FingerprintCaptured += OnFingerprintForSearch;
    }

    private void OnFingerprintForSearch(object? sender, FingerprintCapturedEventArgs e)
    {
        var scanner = App.Services.GetRequiredService<FingerprintService>();
        scanner.FingerprintCaptured -= OnFingerprintForSearch;

        _dispatcher.Invoke(() =>
        {
            IsFingerprintListening = false;
            var match = scanner.Identify(e.Template);

            if (match != null && _fingerprintTemplateMap.TryGetValue(match.Fid, out int personId))
            {
                _ = ResolveFingerprintMatchAsync(personId, match.Score);
            }
            else
            {
                IdentifyResultHeader = "UNKNOWN";
                IdentifyResultColor = "#C49A52";
                IdentifyResultText = "Fingerprint not enrolled in the system.";
                HasIdentifyResult = true;
                ShowEnrolNewOption = true;
                ShowPatientCard = false;
                StatusText = "Fingerprint not recognized.";
                AddLog("Fingerprint search: no match");
            }
        });
    }

    private async Task ResolveFingerprintMatchAsync(int personId, int score)
    {
        try
        {
            var repo = App.Services.GetRequiredService<FaceRepository>();
            var person = await repo.GetPersonByFingerprintIdAsync(
                _fingerprintTemplateMap.FirstOrDefault(x => x.Value == personId).Key);

            if (person == null)
            {
                // Fallback: try loading by person ID directly
                var persons = await repo.GetAllPersonsAsync();
                var summary = persons.FirstOrDefault(p => p.Id == personId);
                if (summary != null)
                    person = await repo.GetPatientByPidAsync(summary.IDCard);
            }

            _dispatcher.Invoke(() =>
            {
                if (person == null)
                {
                    IdentifyResultHeader = "ERROR";
                    IdentifyResultColor = "#B85C56";
                    IdentifyResultText = "Matched fingerprint but patient record not found.";
                    HasIdentifyResult = true;
                    StatusText = "Fingerprint matched but patient not found.";
                    AddLog($"Fingerprint match error: personId={personId} not found");
                    return;
                }

                IdentifyResultHeader = "IDENTIFIED (FINGERPRINT)";
                IdentifyResultColor = "#5B7F62";
                IdentifyResultText = $"Score: {score}";
                HasIdentifyResult = true;
                ShowEnrolNewOption = false;

                SetSelectedPatient(person, "fingerprint");
                NeedsBiometricUpdate = false;

                StatusText = $"Identified by fingerprint: {person.FullName}";
                AddLog($"Fingerprint match: {person.IDCard} {person.FullName} (score={score})");
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() =>
            {
                IdentifyResultHeader = "ERROR";
                IdentifyResultColor = "#B85C56";
                IdentifyResultText = ex.Message;
                HasIdentifyResult = true;
                StatusText = $"Fingerprint search error: {ex.Message}";
            });
            AddLog($"Fingerprint resolve error: {ex.Message}");
        }
    }

    // ─── Step 2: Verify (1:1) ───

    [RelayCommand]
    private void GoToVerify()
    {
        ShowVerifySection = true;
        HasVerifyResult = false;
        FacialChangeChecked = false;
        FacialChangeReason = "";
        PhotoUpdateStatus = "";
    }

    [RelayCommand]
    private async Task VerifyPatientAsync()
    {
        if (!IsCameraRunning)
        {
            StatusText = "Camera must be running to verify.";
            return;
        }

        if (_selectedPatient == null || IsBusy) return;
        IsBusy = true;
        StatusText = $"Verifying against {_selectedPatient.IDCard}...";
        AddLog($"Verify started: PID={_selectedPatient.IDCard}");

        try
        {
            using var frame = _camera.CaptureSnapshot();
            if (frame == null)
            {
                StatusText = "Failed to capture frame.";
                return;
            }

            var pid = _selectedPatient.IDCard;
            var result = await Task.Run(() => _pipeline.VerifyFromFrameAsync(frame, pid));

            _dispatcher.Invoke(() =>
            {
                if (!result.Success)
                {
                    VerifyResultHeader = "ERROR";
                    VerifyResultText = result.Error ?? "Verification failed.";
                    VerifyResultColor = "#B85C56";
                    HasVerifyResult = true;
                    StatusText = result.Error ?? "Verification failed.";
                    AddLog($"Verify: {result.Error}");
                    return;
                }

                if (result.IsVerified)
                {
                    VerifyResultHeader = result.IsHighConfidence ? "VERIFIED (HIGH)" : "VERIFIED";
                    VerifyResultColor = "#5B7F62";
                    VerifyResultText = result.SimilarityText;
                    HasVerifyResult = true;
                    NeedsBiometricUpdate = false;

                    StatusText = $"Verified: {_selectedPatient.FullName} ({result.SimilarityText})";
                    AddLog($"Verified: {pid} {_selectedPatient.FullName} ({result.SimilarityText})");
                }
                else
                {
                    VerifyResultHeader = "NOT VERIFIED";
                    VerifyResultColor = "#B85C56";
                    VerifyResultText = $"Face does not match ({result.SimilarityText})";
                    HasVerifyResult = true;

                    StatusText = $"NOT verified against {pid} ({result.SimilarityText})";
                    AddLog($"Not verified: {pid} ({result.SimilarityText})");
                }
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() =>
            {
                VerifyResultHeader = "ERROR";
                VerifyResultText = ex.Message;
                VerifyResultColor = "#B85C56";
                HasVerifyResult = true;
            });
            AddLog($"Verify error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdatePhotoAsync()
    {
        if (!IsCameraRunning || _selectedPatient == null || IsBusy) return;

        IsBusy = true;
        PhotoUpdateStatus = "";
        StatusText = "Capturing new photo...";

        try
        {
            using var frame = _camera.CaptureSnapshot();
            if (frame == null)
            {
                PhotoUpdateStatus = "Failed to capture frame.";
                return;
            }

            var success = await Task.Run(() => _pipeline.AddFaceSampleAsync(frame, _selectedPatient.Id));

            _dispatcher.Invoke(() =>
            {
                if (success)
                {
                    PhotoUpdateStatus = "Photo updated successfully";
                    NeedsBiometricUpdate = false;
                    StatusText = $"Photo updated for {_selectedPatient.IDCard}";
                    AddLog($"Photo updated: {_selectedPatient.IDCard} {_selectedPatient.FullName}");
                }
                else
                {
                    PhotoUpdateStatus = "Failed — no face detected";
                    StatusText = "Photo update failed: no face detected.";
                    AddLog("Photo update failed: no face detected");
                }
            });
        }
        catch (Exception ex)
        {
            PhotoUpdateStatus = $"Error: {ex.Message}";
            AddLog($"Photo update error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ─── Step 4: Visit ───

    [RelayCommand]
    private void GoToVisit()
    {
        ShowVisitSection = true;
        VisitServiceType = "";
        VisitChiefComplaint = "";
        VisitLogged = false;
    }

    [RelayCommand]
    private async Task LogVisitAsync()
    {
        if (_selectedPatient == null) return;

        if (string.IsNullOrWhiteSpace(VisitServiceType))
        {
            StatusText = "Please select a service type.";
            return;
        }

        IsBusy = true;
        StatusText = "Logging visit...";

        try
        {
            var repo = App.Services.GetRequiredService<FaceRepository>();

            var visit = new Visit
            {
                PersonId = _selectedPatient.Id,
                ServiceType = VisitServiceType,
                ChiefComplaint = string.IsNullOrWhiteSpace(VisitChiefComplaint) ? null : VisitChiefComplaint.Trim(),
                VisitDate = DateTime.UtcNow
            };

            await repo.CreateVisitAsync(visit);

            // Log facial change as a note on the patient record
            if (FacialChangeChecked)
            {
                var reason = string.IsNullOrWhiteSpace(FacialChangeReason)
                    ? "Facial change noted"
                    : FacialChangeReason.Trim();
                var note = $"[{DateTime.UtcNow:yyyy-MM-dd}] {reason}";

                _selectedPatient.Notes = string.IsNullOrEmpty(_selectedPatient.Notes)
                    ? note
                    : $"{_selectedPatient.Notes}\n{note}";
                await repo.UpdatePatientAsync(_selectedPatient);
            }

            _dispatcher.Invoke(() =>
            {
                VisitLogged = true;
                StatusText = $"Visit logged for {_selectedPatient.IDCard} ({VisitServiceType})";
                AddLog($"Visit logged: {_selectedPatient.IDCard} — {VisitServiceType}");
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to log visit: {ex.Message}";
            AddLog($"Visit error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ─── Print Routing Slip ───

    [RelayCommand]
    private void PrintRoutingSlip()
    {
        if (_selectedPatient == null) return;
        AddLog($"Print routing slip: {_selectedPatient.IDCard} — {VisitServiceType}");
        StatusText = "Routing slip sent to printer.";
    }

    // ─── Enrol New ───

    [RelayCommand]
    private void EnrolNew()
    {
        var window = new Views.EnrolmentWindow();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();

        _ = RefreshDatabaseStatsAsync();
    }

    // ─── Start Over ───

    [RelayCommand]
    private void StartOver()
    {
        _selectedPatient = null;
        IdentificationMethod = "";
        NeedsBiometricUpdate = false;
        IsBusy = false;
        IsFingerprintListening = false;

        // Unsubscribe fingerprint handler if listening
        try
        {
            var scanner = App.Services.GetRequiredService<FingerprintService>();
            scanner.FingerprintCaptured -= OnFingerprintForSearch;
        }
        catch { }

        // Identify
        IdentifyResultHeader = "";
        IdentifyResultColor = "#A8A29E";
        IdentifyResultText = "";
        HasIdentifyResult = false;
        ManualSearchQuery = "";
        ManualSearchResults.Clear();
        HasManualSearchResults = false;
        ShowEnrolNewOption = false;

        // Patient card
        ShowPatientCard = false;
        PatientPid = "";
        PatientName = "";
        PatientSex = "";
        PatientAge = "";
        PatientIdentifyTiming = "";

        // Verify
        ShowVerifySection = false;
        VerifyResultHeader = "";
        VerifyResultColor = "#A8A29E";
        VerifyResultText = "";
        HasVerifyResult = false;
        FacialChangeChecked = false;
        FacialChangeReason = "";
        PhotoUpdateStatus = "";

        // Visit
        ShowVisitSection = false;
        VisitServiceType = "";
        VisitChiefComplaint = "";
        VisitLogged = false;

        StatusText = "Ready. Click 'Start Camera' to begin.";
        AddLog("Workflow reset");
    }

    // ──────────────────────────────────────────────
    // Workflow Helpers
    // ──────────────────────────────────────────────

    private void SetSelectedPatient(Person person, string method)
    {
        _selectedPatient = person;
        IdentificationMethod = method;

        PatientPid = person.IDCard;
        PatientName = person.FullName;
        PatientSex = person.Sex switch { 1 => "M", 2 => "F", _ => "" };
        PatientAge = CalculateAge(person);
        ShowPatientCard = true;

        // Reset downstream sections
        ShowVerifySection = false;
        HasVerifyResult = false;
        FacialChangeChecked = false;
        FacialChangeReason = "";
        PhotoUpdateStatus = "";
        ShowVisitSection = false;
        VisitLogged = false;
    }

    private static string CalculateAge(Person person)
    {
        if (person.AgeAtEnrolment.HasValue)
            return $"{person.AgeAtEnrolment}y";
        if (person.DOBYear.HasValue)
        {
            int age = DateTime.Now.Year - person.DOBYear.Value;
            return $"{age}y";
        }
        return "";
    }

    // ──────────────────────────────────────────────
    // Camera Frame Handler
    // ──────────────────────────────────────────────

    /// <summary>
    /// Camera frame handler (runs on capture thread ~30fps).
    ///
    /// Design: Producer-consumer pattern.
    ///   - This handler (producer) writes the latest frame to a shared buffer.
    ///   - OnRender (consumer) reads from the buffer on the UI thread.
    ///   - Camera thread NEVER waits for the UI thread → no blocking.
    ///   - If camera produces faster than UI renders, intermediate frames are dropped.
    ///
    /// Previous approach created a new frozen BitmapSource per frame (30 allocs/sec,
    /// ~27MB/sec GC pressure) and queued 30 BeginInvoke calls/sec, overwhelming the
    /// WPF dispatcher and causing the camera feed to freeze.
    /// </summary>
    private void OnFrameCaptured(object? sender, FrameEventArgs e)
    {
        bool ownershipTransferred = false;
        try
        {
            // Offload AI processing to thread pool (never blocks capture thread)
            if (e.ShouldProcess)
            {
                var processingFrame = e.Frame.Clone();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _pipeline.ProcessFrameAsync(processingFrame);
                    }
                    finally
                    {
                        processingFrame.Dispose();
                    }
                });
            }

            // Draw overlays from last processed results (fast, ~1ms).
            // Wrapped in its own try/catch so overlay failures don't prevent display.
            try
            {
                _pipeline.DrawOverlays(e.Frame);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Overlay error: {ex.Message}");
            }

            // Transfer frame ownership to display buffer (zero-copy, no allocation).
            // OnRender will pick up the latest frame on the next WPF render tick.
            lock (_displayLock)
            {
                _latestDisplayFrame?.Dispose();
                _latestDisplayFrame = e.Frame;
            }
            ownershipTransferred = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Frame error: {ex.Message}");
        }
        finally
        {
            if (!ownershipTransferred)
                e.Frame.Dispose();
        }
    }

    /// <summary>
    /// WPF render callback (runs on UI thread, synced with rendering pipeline).
    ///
    /// Reads the latest frame from the display buffer and copies pixels into
    /// a single WriteableBitmap (no per-frame allocation, no GC pressure).
    /// WriteableBitmap.Lock/memcpy/Unlock is ~1-2ms for 640x480.
    /// </summary>
    private void OnRender(object? sender, EventArgs e)
    {
        Mat? frame;
        lock (_displayLock)
        {
            frame = _latestDisplayFrame;
            _latestDisplayFrame = null; // Take ownership
        }

        if (frame == null) return;

        try
        {
            if (_writeableBitmap == null ||
                _writeableBitmap.PixelWidth != frame.Width ||
                _writeableBitmap.PixelHeight != frame.Height)
            {
                // First frame or resolution changed — create WriteableBitmap
                _writeableBitmap = WpfImageHelper.CreateWriteableBitmap(frame);
                CameraFrame = _writeableBitmap; // Bind once; WPF re-renders on dirty rect
            }
            else
            {
                // Update pixels in-place (Lock → memcpy → AddDirtyRect → Unlock)
                WpfImageHelper.UpdateWriteableBitmap(frame, _writeableBitmap);
            }

            FpsText = $"FPS: {_camera.CurrentFps:F1}";
        }
        catch (Exception ex)
        {
            // Don't let render errors crash the app — just log and skip this frame
            System.Diagnostics.Debug.WriteLine($"[Render] Error: {ex.Message}");
        }
        finally
        {
            frame.Dispose();
        }
    }

    // ──────────────────────────────────────────────
    // Pipeline Results Handler
    // ──────────────────────────────────────────────

    private void OnResultsUpdated(object? sender, IReadOnlyList<RecognitionResult> results)
    {
        _dispatcher.BeginInvoke(() =>
        {
            // Update timing display
            TimingText = $"Detect: {_pipeline.LastDetectionTime.TotalMilliseconds:F0}ms | " +
                         $"Embed: {_pipeline.LastEmbeddingTime.TotalMilliseconds:F0}ms | " +
                         $"Search: {_pipeline.LastSearchTime.TotalMilliseconds:F0}ms | " +
                         $"Total: {_pipeline.LastTotalTime.TotalMilliseconds:F0}ms";

            // Update liveness
            LivenessText = _pipeline.LivenessStatusText;

            // Update results panel
            CurrentResults.Clear();
            foreach (var result in results)
            {
                CurrentResults.Add(new RecognitionResultViewModel(result));

                // Log recognized faces (avoid spamming — only log first occurrence)
                if (result.IsRecognized && result.Person != null)
                {
                    // Dedupe by person name — similarity % changes each frame
                    AddLogIfNew(
                        $"Recognized: {result.Person.FullName} ({result.SimilarityText})",
                        $"recognized_{result.Person.Id}");
                }
            }

            if (results.Count == 0)
            {
                StatusText = "No faces detected";
            }
            else
            {
                var recognized = results.Count(r => r.IsRecognized);
                StatusText = $"Detected {results.Count} face(s), {recognized} recognized";
            }
        });
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private async Task RefreshDatabaseStatsAsync()
    {
        try
        {
            var repo = App.Services.GetRequiredService<FaceRepository>();
            var stats = await repo.GetStatsAsync();
            var searchMode = repo.UseVectorSearch ? "DiskANN" : "KNN";
            _dispatcher.Invoke(() =>
            {
                DatabaseText = $"DB: {stats.TotalPersons} persons, " +
                               $"{stats.TotalEmbeddings} samples [{searchMode}]";
            });
        }
        catch { }
    }

    private void AddLog(string message)
    {
        _dispatcher.BeginInvoke(() =>
        {
            ActivityLog.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
            // Keep log size manageable
            while (ActivityLog.Count > 100)
                ActivityLog.RemoveAt(ActivityLog.Count - 1);
        });
    }

    private string? _lastLogKey;
    /// <summary>
    /// Add a log entry only if the dedup key is different from the last one.
    /// The key strips variable parts (like similarity %) to avoid spam.
    /// </summary>
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
        _camera.Stop();

        // Clean up fingerprint event subscriptions
        try
        {
            var scanner = App.Services.GetRequiredService<FingerprintService>();
            scanner.FingerprintCaptured -= OnFingerprintForSearch;
        }
        catch { }

        lock (_displayLock)
        {
            _latestDisplayFrame?.Dispose();
            _latestDisplayFrame = null;
        }
    }
}

/// <summary>
/// View model wrapper for a single recognition result (for data binding).
/// </summary>
public class RecognitionResultViewModel
{
    public string Name { get; }
    public string PID { get; }
    public string Similarity { get; }
    public string Status { get; }
    public string StatusColor { get; }
    public Person? Person { get; }

    public RecognitionResultViewModel(RecognitionResult result)
    {
        Person = result.Person;
        Name = result.Person?.FullName ?? "Unknown";
        PID = result.Person?.IDCard ?? "";
        Similarity = result.SimilarityText;

        if (result.IsSpoofDetected)
        {
            Status = "SPOOF";
            StatusColor = "#B85C56";
        }
        else if (result.IsHighConfidence)
        {
            Status = result.IsLive ? "LIVE" : "VERIFYING";
            StatusColor = result.IsLive ? "#5B7F62" : "#C49A52";
        }
        else if (result.IsRecognized)
        {
            Status = result.IsLive ? "MATCH" : "VERIFYING";
            StatusColor = result.IsLive ? "#5B7F62" : "#C49A52";
        }
        else
        {
            Status = "UNKNOWN";
            StatusColor = "#A8A29E";
        }
    }
}
