using System.Windows;
using FaceRecApp.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FaceRecApp.WPF.Views;

/// <summary>
/// Database management window — shows all registered persons, stats, and allows deletion.
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
            // Load persons
            var persons = await _repository.GetAllPersonsAsync();
            PersonsGrid.ItemsSource = persons;

            // Load stats
            var stats = await _repository.GetStatsAsync();
            StatPersons.Text = $"Persons: {stats.TotalPersons}";
            StatSamples.Text = $"Face Samples: {stats.TotalEmbeddings} (avg: {stats.AverageSamplesPerPerson:F1}/person)";
            StatRecognitions.Text = $"Total Recognitions: {stats.TotalRecognitions}";
            StatRate.Text = $"Recognition Rate: {stats.RecognitionRate:F1}%";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load data: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await LoadDataAsync();
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (PersonsGrid.SelectedItem is not PersonSummary selected)
        {
            MessageBox.Show("Please select a person to delete.", "No Selection",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Are you sure you want to delete '{selected.Name}' and all their face samples?\n\nThis cannot be undone.",
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

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
