using System.Windows;
using FaceRecApp.Core.Data;
using FaceRecApp.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FaceRecApp.WPF.Views;

public partial class BenchmarkWindow : Window
{
    private readonly BenchmarkService _benchmark;

    public BenchmarkWindow()
    {
        InitializeComponent();

        var dbFactory = App.Services.GetRequiredService<IDbContextFactory<FaceDbContext>>();
        var repo = App.Services.GetRequiredService<FaceRepository>();
        _benchmark = new BenchmarkService(dbFactory, repo);
    }

    private async void OnRunBenchmark(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Running benchmarks...");

        try
        {
            var report = await Task.Run(() => _benchmark.RunFullBenchmarkAsync(iterations: 20));
            ResultsText.Text = report.ToString();
        }
        catch (Exception ex)
        {
            ResultsText.Text = $"Benchmark failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnPopulate100(object sender, RoutedEventArgs e)
    {
        await PopulateAsync(100, 3);
    }

    private async void OnPopulate1000(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "This will add 1,000 synthetic persons with 3 samples each (3,000 embeddings).\n\nThis may take 30-60 seconds. Continue?",
            "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        await PopulateAsync(1000, 3);
    }

    private async Task PopulateAsync(int count, int samples)
    {
        SetBusy(true, $"Populating {count} synthetic persons ({count * samples} embeddings)...");

        try
        {
            var total = await Task.Run(() =>
                _benchmark.PopulateSyntheticDataAsync(count, samples));

            ResultsText.Text = $"Added {count} synthetic persons with {total} total embeddings.\n\n" +
                               "Click 'Run Benchmarks' to test performance with this data.";
        }
        catch (Exception ex)
        {
            ResultsText.Text = $"Population failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnCleanup(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "This will remove all synthetic/benchmark test data.\nReal registrations will not be affected.",
            "Confirm Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true, "Cleaning up synthetic data...");

        try
        {
            await Task.Run(() => _benchmark.CleanupSyntheticDataAsync());
            ResultsText.Text = "All synthetic benchmark data removed.";
        }
        catch (Exception ex)
        {
            ResultsText.Text = $"Cleanup failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        RunBenchmarkBtn.IsEnabled = !busy;
        PopulateBtn.IsEnabled = !busy;
        Populate1kBtn.IsEnabled = !busy;
        CleanupBtn.IsEnabled = !busy;

        if (busy && message != null)
            ResultsText.Text = $"{message}\n\nPlease wait...";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
