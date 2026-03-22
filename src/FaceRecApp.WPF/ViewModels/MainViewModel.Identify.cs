using CommunityToolkit.Mvvm.Input;
using FaceRecApp.Core.Entities;
using FaceRecApp.Core.Services;
using FaceRecApp.WPF.Services;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FaceRecApp.WPF.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task IdentifyAsync()
    {
        if (!IsCameraRunning) { StatusText = "Camera must be running to identify."; AddLog("Identify failed: camera not running"); return; }
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Identifying...";
        AddLog("Identify started");

        try
        {
            using var frame = _camera.CaptureSnapshot();
            if (frame == null) { StatusText = "Failed to capture frame."; AddLog("Identify failed: no frame"); return; }

            var result = await Task.Run(() => _pipeline.IdentifyFromFrameAsync(frame));

            _dispatcher.Invoke(() =>
            {
                PatientIdentifyTiming = $"{result.Elapsed.TotalMilliseconds:F0}ms";

                if (!result.Success)
                {
                    IdentifyResultHeader = "ERROR"; IdentifyResultText = result.Error ?? "Identification failed.";
                    IdentifyResultColor = "#B85C56"; HasIdentifyResult = true; ShowEnrolNewOption = false;
                    StatusText = result.Error ?? "Identification failed."; AddLog($"Identify: {result.Error}"); return;
                }

                if (result.IsIdentified && result.Recognition?.Patient != null)
                {
                    var person = result.Recognition.Patient;
                    IdentifyResultHeader = result.Recognition.IsHighConfidence ? "IDENTIFIED (HIGH)" : "IDENTIFIED";
                    IdentifyResultColor = "#5B7F62"; IdentifyResultText = result.Recognition.SimilarityText;
                    HasIdentifyResult = true; ShowEnrolNewOption = false;
                    SetSelectedPatient(person, "face"); NeedsBiometricUpdate = false;
                    StatusText = $"Identified: {person.FullName} ({result.Recognition.SimilarityText})";
                    AddLog($"Identified: {person.IDCard} {person.FullName} ({result.Recognition.SimilarityText})");
                }
                else
                {
                    _selectedPatient = null;
                    IdentifyResultHeader = "UNKNOWN"; IdentifyResultColor = "#C49A52";
                    IdentifyResultText = "Face not enrolled in the system.";
                    HasIdentifyResult = true; ShowEnrolNewOption = true; ShowPatientCard = false;
                    StatusText = "Face not recognized.";
                    AddLog($"Identify: unknown face ({result.Recognition?.SimilarityText ?? "N/A"})");
                }
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() => { IdentifyResultHeader = "ERROR"; IdentifyResultText = ex.Message; IdentifyResultColor = "#B85C56"; HasIdentifyResult = true; });
            AddLog($"Identify error: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ManualSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(ManualSearchQuery)) return;
        IsBusy = true;
        StatusText = $"Searching for \"{ManualSearchQuery.Trim()}\"...";
        try
        {
            var repo = App.Services.GetRequiredService<FaceRepository>();
            var results = await repo.SearchPatientsByNameAsync(ManualSearchQuery.Trim());
            _dispatcher.Invoke(() =>
            {
                ManualSearchResults.Clear();
                foreach (var r in results) ManualSearchResults.Add(r);
                HasManualSearchResults = results.Count > 0;
                StatusText = results.Count > 0 ? $"Found {results.Count} patient(s)" : "No patients found matching that name.";
                AddLog($"Search: \"{ManualSearchQuery.Trim()}\" -> {results.Count} result(s)");
            });
        }
        catch (Exception ex) { StatusText = $"Search failed: {ex.Message}"; AddLog($"Search error: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SelectPatientAsync(PatientSummary summary)
    {
        if (summary == null) return;
        IsBusy = true;
        try
        {
            var repo = App.Services.GetRequiredService<FaceRepository>();
            var person = await repo.GetPatientByPidAsync(summary.IDCard);
            if (person == null) { StatusText = $"Patient {summary.IDCard} not found."; return; }
            _dispatcher.Invoke(() =>
            {
                SetSelectedPatient(person, "manual"); NeedsBiometricUpdate = true; HasManualSearchResults = false;
                StatusText = $"Selected: {person.FullName} ({person.IDCard})";
                AddLog($"Selected (manual): {person.IDCard} {person.FullName}");
            });
        }
        catch (Exception ex) { StatusText = $"Failed to load patient: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task PhotoSearchAsync()
    {
        var dialogService = App.Services.GetRequiredService<IDialogService>();
        var fileName = dialogService.ShowOpenFileDialog("Select a patient photo", "Image files|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*");
        if (fileName == null) return;

        IsBusy = true; StatusText = "Searching by photo...";
        AddLog($"Photo search: {System.IO.Path.GetFileName(fileName)}");
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<Rgb24>(fileName);
            var result = await Task.Run(() => _pipeline.IdentifyFromImageAsync(image));
            _dispatcher.Invoke(() =>
            {
                PatientIdentifyTiming = $"{result.Elapsed.TotalMilliseconds:F0}ms";
                if (!result.Success)
                {
                    IdentifyResultHeader = "ERROR"; IdentifyResultText = result.Error ?? "Photo search failed.";
                    IdentifyResultColor = "#B85C56"; HasIdentifyResult = true; ShowEnrolNewOption = false;
                    StatusText = result.Error ?? "Photo search failed."; AddLog($"Photo search: {result.Error}"); return;
                }
                if (result.IsIdentified && result.Recognition?.Patient != null)
                {
                    var person = result.Recognition.Patient;
                    IdentifyResultHeader = "IDENTIFIED (PHOTO)"; IdentifyResultColor = "#5B7F62";
                    IdentifyResultText = result.Recognition.SimilarityText;
                    HasIdentifyResult = true; ShowEnrolNewOption = false;
                    SetSelectedPatient(person, "photo"); NeedsBiometricUpdate = true;
                    StatusText = $"Identified from photo: {person.FullName}";
                    AddLog($"Photo match: {person.IDCard} {person.FullName} ({result.Recognition.SimilarityText})");
                }
                else
                {
                    _selectedPatient = null; IdentifyResultHeader = "UNKNOWN"; IdentifyResultColor = "#C49A52";
                    IdentifyResultText = "No match found for this photo.";
                    HasIdentifyResult = true; ShowEnrolNewOption = true; ShowPatientCard = false;
                    StatusText = "Photo not recognized."; AddLog("Photo search: no match");
                }
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() => { IdentifyResultHeader = "ERROR"; IdentifyResultText = ex.Message; IdentifyResultColor = "#B85C56"; HasIdentifyResult = true; });
            AddLog($"Photo search error: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task FingerprintSearchAsync()
    {
        var scanner = App.Services.GetRequiredService<FingerprintService>();
        if (!scanner.IsDeviceOpen)
        {
            int devCount = scanner.Initialize();
            if (devCount < 0) { IdentifyResultHeader = "ERROR"; IdentifyResultColor = "#B85C56"; IdentifyResultText = "Fingerprint SDK failed to load."; HasIdentifyResult = true; StatusText = "Fingerprint SDK load failed."; AddLog("Fingerprint search: SDK load failed"); return; }
            if (devCount == 0) { IdentifyResultHeader = "UNAVAILABLE"; IdentifyResultColor = "#A8A29E"; IdentifyResultText = "No fingerprint scanner connected."; HasIdentifyResult = true; StatusText = "Fingerprint scanner not found."; AddLog("Fingerprint search: no scanner detected"); return; }
            if (!scanner.OpenDevice(0)) { IdentifyResultHeader = "ERROR"; IdentifyResultColor = "#B85C56"; IdentifyResultText = "Failed to open fingerprint scanner."; HasIdentifyResult = true; StatusText = "Failed to open fingerprint scanner."; AddLog("Fingerprint search: failed to open device"); return; }
        }
        if (scanner.CacheCount == 0)
        {
            StatusText = "Loading fingerprint database...";
            var repo = App.Services.GetRequiredService<FaceRepository>();
            var templates = await repo.GetAllFingerprintTemplatesAsync();
            if (templates.Count == 0) { IdentifyResultHeader = "EMPTY"; IdentifyResultColor = "#C49A52"; IdentifyResultText = "No fingerprints enrolled."; HasIdentifyResult = true; StatusText = "No fingerprint templates in database."; AddLog("Fingerprint search: database empty"); return; }
            _fingerprintTemplateMap = templates.ToDictionary(t => t.Key, t => t.Value.PID);
            scanner.LoadTemplates(templates.ToDictionary(t => t.Key, t => t.Value.Template));
            AddLog($"Loaded {templates.Count} fingerprint template(s) into cache");
        }
        IsFingerprintListening = true; StatusText = "Place finger on scanner..."; AddLog("Fingerprint search: waiting for finger");
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
            if (match != null && _fingerprintTemplateMap.TryGetValue(match.Fid, out string? pid))
                _ = ResolveFingerprintMatchAsync(pid, match.Score);
            else
            {
                IdentifyResultHeader = "UNKNOWN"; IdentifyResultColor = "#C49A52";
                IdentifyResultText = "Fingerprint not enrolled in the system.";
                HasIdentifyResult = true; ShowEnrolNewOption = true; ShowPatientCard = false;
                StatusText = "Fingerprint not recognized."; AddLog("Fingerprint search: no match");
            }
        });
    }

    private async Task ResolveFingerprintMatchAsync(string pid, int score)
    {
        try
        {
            var repo = App.Services.GetRequiredService<FaceRepository>();
            var person = await repo.GetPatientByPidAsync(pid);
            _dispatcher.Invoke(() =>
            {
                if (person == null) { IdentifyResultHeader = "ERROR"; IdentifyResultColor = "#B85C56"; IdentifyResultText = "Matched fingerprint but patient record not found."; HasIdentifyResult = true; StatusText = "Fingerprint matched but patient not found."; AddLog($"Fingerprint match error: PID={pid} not found"); return; }
                IdentifyResultHeader = "IDENTIFIED (FINGERPRINT)"; IdentifyResultColor = "#5B7F62";
                IdentifyResultText = $"Score: {score}"; HasIdentifyResult = true; ShowEnrolNewOption = false;
                SetSelectedPatient(person, "fingerprint"); NeedsBiometricUpdate = false;
                StatusText = $"Identified by fingerprint: {person.FullName}";
                AddLog($"Fingerprint match: {person.IDCard} {person.FullName} (score={score})");
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() => { IdentifyResultHeader = "ERROR"; IdentifyResultColor = "#B85C56"; IdentifyResultText = ex.Message; HasIdentifyResult = true; StatusText = $"Fingerprint search error: {ex.Message}"; });
            AddLog($"Fingerprint resolve error: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenRegister()
    {
        var dialogService = App.Services.GetRequiredService<IDialogService>();
        dialogService.ShowEnrolmentDialog();
        _ = RefreshDatabaseStatsAsync();
    }

    [RelayCommand]
    private void OpenDatabase()
    {
        var dialogService = App.Services.GetRequiredService<IDialogService>();
        dialogService.ShowDatabaseDialog();
        _ = RefreshDatabaseStatsAsync();
    }

    [RelayCommand]
    private void OpenBenchmark()
    {
        var dialogService = App.Services.GetRequiredService<IDialogService>();
        dialogService.ShowBenchmarkDialog();
        _ = RefreshDatabaseStatsAsync();
    }

    [RelayCommand]
    private void EnrolNew()
    {
        var dialogService = App.Services.GetRequiredService<IDialogService>();
        dialogService.ShowEnrolmentDialog();
        _ = RefreshDatabaseStatsAsync();
    }

    [RelayCommand]
    private void StartOver()
    {
        _selectedPatient = null; IdentificationMethod = ""; NeedsBiometricUpdate = false; IsBusy = false; IsFingerprintListening = false;
        try { var scanner = App.Services.GetRequiredService<FingerprintService>(); scanner.FingerprintCaptured -= OnFingerprintForSearch; } catch { }

        IdentifyResultHeader = ""; IdentifyResultColor = "#A8A29E"; IdentifyResultText = "";
        HasIdentifyResult = false; ManualSearchQuery = ""; ManualSearchResults.Clear();
        HasManualSearchResults = false; ShowEnrolNewOption = false;

        ShowPatientCard = false; PatientPid = ""; PatientName = ""; PatientSex = ""; PatientAge = ""; PatientIdentifyTiming = "";

        ShowVerifySection = false; VerifyResultHeader = ""; VerifyResultColor = "#A8A29E";
        VerifyResultText = ""; HasVerifyResult = false; FacialChangeChecked = false;
        FacialChangeReason = ""; PhotoUpdateStatus = "";

        ShowVisitSection = false; VisitServiceType = ""; VisitChiefComplaint = ""; VisitLogged = false;

        StatusText = "Ready. Click 'Start Camera' to begin."; AddLog("Workflow reset");
    }
}
