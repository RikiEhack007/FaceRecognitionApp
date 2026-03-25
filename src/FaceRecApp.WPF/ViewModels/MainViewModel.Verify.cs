using CommunityToolkit.Mvvm.Input;
using FaceRecApp.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FaceRecApp.WPF.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void GoToVerify()
    {
        HasVerifyResult = false; FacialChangeChecked = false;
        FacialChangeReason = ""; PhotoUpdateStatus = "";
    }

    [RelayCommand]
    private async Task VerifyPatientAsync()
    {
        if (!IsCameraRunning) { StatusText = "Camera must be running to verify."; return; }
        if (_selectedPatient == null || IsBusy) return;
        IsBusy = true;
        StatusText = $"Verifying against {_selectedPatient.IDCard}...";
        AddLog($"Verify started: PID={_selectedPatient.IDCard}");
        try
        {
            using var frame = _camera.CaptureSnapshot();
            if (frame == null) { StatusText = "Failed to capture frame."; return; }
            var pid = _selectedPatient.IDCard;
            var result = await Task.Run(() => _pipeline.VerifyFromFrameAsync(frame, pid));
            _dispatcher.Invoke(() =>
            {
                if (!result.Success)
                {
                    VerifyResultHeader = "ERROR"; VerifyResultText = result.Error ?? "Verification failed.";
                    VerifyResultColor = "#B85C56"; HasVerifyResult = true;
                    StatusText = result.Error ?? "Verification failed."; AddLog($"Verify: {result.Error}"); return;
                }
                if (result.IsVerified)
                {
                    VerifyResultHeader = result.IsHighConfidence ? "VERIFIED (HIGH)" : "VERIFIED";
                    VerifyResultColor = "#5B7F62"; VerifyResultText = result.SimilarityText;
                    HasVerifyResult = true; NeedsBiometricUpdate = false;
                    StatusText = $"Verified: {_selectedPatient.FullName} ({result.SimilarityText})";
                    AddLog($"Verified: {pid} {_selectedPatient.FullName} ({result.SimilarityText})");
                }
                else
                {
                    VerifyResultHeader = "NOT VERIFIED"; VerifyResultColor = "#B85C56";
                    VerifyResultText = $"Face does not match ({result.SimilarityText})"; HasVerifyResult = true;
                    StatusText = $"NOT verified against {pid} ({result.SimilarityText})";
                    AddLog($"Not verified: {pid} ({result.SimilarityText})");
                }
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() => { VerifyResultHeader = "ERROR"; VerifyResultText = ex.Message; VerifyResultColor = "#B85C56"; HasVerifyResult = true; });
            AddLog($"Verify error: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task UpdatePhotoAsync()
    {
        if (!IsCameraRunning || _selectedPatient == null || IsBusy) return;
        IsBusy = true; PhotoUpdateStatus = ""; StatusText = "Capturing new photo...";
        try
        {
            using var frame = _camera.CaptureSnapshot();
            if (frame == null) { PhotoUpdateStatus = "Failed to capture frame."; return; }
            var pid = _selectedPatient.IDCard;
            var success = await Task.Run(() => _pipeline.AddFaceSampleAsync(frame, pid));
            _dispatcher.Invoke(() =>
            {
                if (success)
                {
                    PhotoUpdateStatus = "Photo updated successfully"; NeedsBiometricUpdate = false;
                    StatusText = $"Photo updated for {_selectedPatient.IDCard}";
                    AddLog($"Photo updated: {_selectedPatient.IDCard} {_selectedPatient.FullName}");
                }
                else
                {
                    PhotoUpdateStatus = "Failed — no face detected";
                    StatusText = "Photo update failed: no face detected."; AddLog("Photo update failed: no face detected");
                }
            });
        }
        catch (Exception ex) { PhotoUpdateStatus = $"Error: {ex.Message}"; AddLog($"Photo update error: {ex.Message}"); }
        finally { IsBusy = false; }
    }
}
