using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FaceRecApp.WPF.ViewModels;

public partial class MainViewModel
{
    // ── Step 3: Enrol ──

    [ObservableProperty] private bool _consentGiven;
    [ObservableProperty] private bool _faceCaptured;
    [ObservableProperty] private bool _fingerprintsCaptured;
    [ObservableProperty] private bool _deduplicationPassed;

    private void ResetEnrolState()
    {
        ConsentGiven = false;
        FaceCaptured = false;
        FingerprintsCaptured = false;
        DeduplicationPassed = false;
    }

    [RelayCommand]
    private void GoToEnrol()
    {
        CurrentWorkflowStep = 3;
    }

    [RelayCommand]
    private void ContinueFromVerify()
    {
        if (IsEnrolStepRequired)
            CurrentWorkflowStep = 3;
        else
            CurrentWorkflowStep = 4;
    }
}
