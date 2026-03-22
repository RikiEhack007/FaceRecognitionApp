using System.Windows;
using FaceRecApp.Core.Entities;
using FaceRecApp.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FaceRecApp.WPF.Views;

/// <summary>
/// Visit logging window — records a patient visit with service type and chief complaint.
/// Shows visit history for the patient.
/// </summary>
public partial class VisitWindow : System.Windows.Window
{
    private readonly FaceRepository _repository;
    private readonly Patient _patient;

    public VisitWindow(Patient patient)
    {
        InitializeComponent();

        _repository = App.Services.GetRequiredService<FaceRepository>();
        _patient = patient;

        // Display patient info
        PatientPidLabel.Text = patient.IDCard;
        PatientNameLabel.Text = patient.FullName;
        PatientSexLabel.Text = patient.Sex == 1 ? "Male" : patient.Sex == 2 ? "Female" : "";

        // Populate service type dropdown
        foreach (var serviceType in BiometricRemarks.ServiceTypes)
            ServiceTypeCombo.Items.Add(serviceType);
        ServiceTypeCombo.SelectedIndex = 0;

        Loaded += async (_, _) => await LoadVisitHistory();
    }

    private async Task LoadVisitHistory()
    {
        try
        {
            var visits = await _repository.GetPatientVisitsAsync(_patient.IDCard);
            VisitHistoryList.ItemsSource = visits;
        }
        catch { }
    }

    private async void OnLogVisitClick(object sender, RoutedEventArgs e)
    {
        if (ServiceTypeCombo.SelectedItem == null)
        {
            MessageBox.Show("Please select a service type.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LogVisitButton.IsEnabled = false;

        try
        {
            var visit = new Visit
            {
                PID = _patient.IDCard,
                Date = DateTime.UtcNow,
                ServiceType = ServiceTypeCombo.SelectedItem.ToString()!,
                ChiefComplaint = string.IsNullOrWhiteSpace(ChiefComplaintInput.Text)
                    ? null
                    : ChiefComplaintInput.Text.Trim(),
                CreatedBy = Environment.UserName,
                CreatedDate = DateTime.UtcNow
            };

            await _repository.CreateVisitAsync(visit);

            // Refresh history and clear form
            await LoadVisitHistory();
            ChiefComplaintInput.Clear();

            MessageBox.Show("Visit logged successfully!", "Success",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to log visit: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LogVisitButton.IsEnabled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
