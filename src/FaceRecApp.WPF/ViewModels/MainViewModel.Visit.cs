using CommunityToolkit.Mvvm.Input;
using FaceRecApp.Core.Entities;
using FaceRecApp.Core.Services;
using FaceRecApp.WPF.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FaceRecApp.WPF.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void GoToVisit()
    {
        ShowVisitSection = true; VisitServiceType = ""; VisitChiefComplaint = ""; VisitLogged = false;
    }

    [RelayCommand]
    private async Task LogVisitAsync()
    {
        if (_selectedPatient == null) return;
        if (string.IsNullOrWhiteSpace(VisitServiceType)) { StatusText = "Please select a service type."; return; }
        IsBusy = true; StatusText = "Logging visit...";
        try
        {
            var repo = App.Services.GetRequiredService<FaceRepository>();
            var visit = new Visit
            {
                PID = _selectedPatient.IDCard, ServiceType = VisitServiceType,
                ChiefComplaint = string.IsNullOrWhiteSpace(VisitChiefComplaint) ? null : VisitChiefComplaint.Trim(),
                Date = DateTime.UtcNow, CreatedBy = Environment.UserName, CreatedDate = DateTime.UtcNow,
            };
            await repo.CreateVisitAsync(visit);
            if (FacialChangeChecked)
            {
                var reason = string.IsNullOrWhiteSpace(FacialChangeReason) ? "Facial change noted" : FacialChangeReason.Trim();
                var note = $"[{DateTime.UtcNow:yyyy-MM-dd}] {reason}";
                _selectedPatient.Note = string.IsNullOrEmpty(_selectedPatient.Note) ? note : $"{_selectedPatient.Note}\n{note}";
                await repo.UpdatePatientAsync(_selectedPatient);
            }
            _dispatcher.Invoke(() =>
            {
                VisitLogged = true;
                StatusText = $"Visit logged for {_selectedPatient.IDCard} ({VisitServiceType})";
                AddLog($"Visit logged: {_selectedPatient.IDCard} -- {VisitServiceType}");
            });
        }
        catch (Exception ex) { StatusText = $"Failed to log visit: {ex.Message}"; AddLog($"Visit error: {ex.Message}"); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void PrintRoutingSlip()
    {
        if (_selectedPatient == null) return;
        AddLog($"Print routing slip: {_selectedPatient.IDCard} -- {VisitServiceType}");
        StatusText = "Routing slip sent to printer.";
    }

    private void SetSelectedPatient(Patient patient, string method)
    {
        _selectedPatient = patient; IdentificationMethod = method;
        PatientPid = patient.IDCard; PatientName = patient.FullName ?? "";
        PatientSex = patient.Sex switch { 1 => "M", 2 => "F", _ => "" };
        PatientAge = CalculateAge(patient); ShowPatientCard = true;
        ShowVerifySection = false; HasVerifyResult = false; FacialChangeChecked = false;
        FacialChangeReason = ""; PhotoUpdateStatus = ""; ShowVisitSection = false; VisitLogged = false;
    }

    private static string CalculateAge(Patient patient)
    {
        if (patient.Age.HasValue) return $"{patient.Age}y";
        if (patient.DOB_year.HasValue) return $"{DateTime.Now.Year - patient.DOB_year.Value}y";
        return "";
    }

    [RelayCommand]
    private void ResetLiveness()
    {
        _pipeline.ResetLiveness(); LivenessText = "Liveness reset — waiting for blink..."; AddLog("Liveness reset");
    }

    [RelayCommand]
    private void ClearLog() { _dispatcher.Invoke(() => ActivityLog.Clear()); }

    [RelayCommand]
    private void ToggleTheme()
    {
        var themeService = App.Services.GetRequiredService<ThemeService>();
        themeService.ToggleTheme(); IsDarkTheme = themeService.IsDark;
        AddLog($"Theme: {(IsDarkTheme ? "Dark" : "Light")}");
    }
}
