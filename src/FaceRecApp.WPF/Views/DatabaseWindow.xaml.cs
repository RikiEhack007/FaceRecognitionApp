using System.Windows;
using System.Windows.Input;
using FaceRecApp.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FaceRecApp.WPF.Views;

/// <summary>
/// Patient database management window — shows all patients, search, stats, and allows deletion.
/// </summary>
public partial class DatabaseWindow : Window
{
    private readonly FaceRepository _repository;

    public DatabaseWindow()
    {
        InitializeComponent();
        _repository = App.Services.GetRequiredService<FaceRepository>();
        Loaded += async (_, _) => await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        try
        {
            var persons = await _repository.GetAllPersonsAsync();
            PersonsGrid.ItemsSource = persons;

            var stats = await _repository.GetStatsAsync();
            StatPersons.Text = $"Patients: {stats.TotalPersons}";
            StatSamples.Text = $"Face Samples: {stats.TotalEmbeddings} (avg: {stats.AverageSamplesPerPerson:F1}/patient)";
            StatRecognitions.Text = $"Total Recognitions: {stats.TotalRecognitions}";
            StatRate.Text = $"Recognition Rate: {stats.RecognitionRate:F1}%";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load data: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        await PerformSearch();
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await PerformSearch();
    }

    private async Task PerformSearch()
    {
        var query = SearchInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            await LoadDataAsync();
            return;
        }

        try
        {
            var results = await _repository.SearchPatientsByNameAsync(query);
            PersonsGrid.ItemsSource = results;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Search failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        SearchInput.Clear();
        await LoadDataAsync();
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (PersonsGrid.SelectedItem is not PersonSummary selected)
        {
            MessageBox.Show("Please select a patient to delete.", "No Selection",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Are you sure you want to delete '{selected.Name}' (PID: {selected.IDCard}) and all their records?\n\nThis cannot be undone.",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            await _repository.DeletePersonAsync(selected.Id);
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to delete: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnViewVisitsClick(object sender, RoutedEventArgs e)
    {
        if (PersonsGrid.SelectedItem is not PersonSummary selected)
        {
            MessageBox.Show("Please select a patient to view visits.", "No Selection",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var patient = await _repository.GetPatientByPidAsync(selected.IDCard);
            if (patient == null)
            {
                MessageBox.Show("Patient not found.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var visitWindow = new VisitWindow(patient);
            visitWindow.Owner = this;
            visitWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load patient: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
