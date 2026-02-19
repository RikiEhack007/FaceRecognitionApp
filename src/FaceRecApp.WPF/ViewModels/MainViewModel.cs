using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FaceRecApp.Core.Services;
using FaceRecApp.WPF.Helpers;
using Microsoft.Extensions.DependencyInjection;
using OpenCvSharp;

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

    // ──────────────────────────────────────────────
    // Constructor
    // ──────────────────────────────────────────────

    public MainViewModel()
    {
        _camera = App.Services.GetRequiredService<CameraService>();
        _pipeline = App.Services.GetRequiredService<RecognitionPipeline>();
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Subscribe to camera frames
        _camera.FrameCaptured += OnFrameCaptured;
        _camera.CameraError += (_, msg) => AddLog($"❌ Camera: {msg}");

        // Subscribe to pipeline results
        _pipeline.ResultsUpdated += OnResultsUpdated;
        _pipeline.ProcessingError += (_, msg) => AddLog($"⚠️ {msg}");

        // Initial database stats
        _ = RefreshDatabaseStatsAsync();
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
            IsCameraRunning = false;
            StatusText = "Camera stopped.";
            FpsText = "FPS: --";
            AddLog("📷 Camera stopped");
        }
        else
        {
            bool success = _camera.Start(0);
            if (success)
            {
                IsCameraRunning = true;
                StatusText = "Camera running — detecting faces...";
                AddLog("📷 Camera started");
            }
            else
            {
                StatusText = "Failed to open camera. Check connection.";
                AddLog("❌ Camera failed to start");
            }
        }
    }

    [RelayCommand]
    private void OpenRegister()
    {
        var window = new Views.RegisterWindow();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();

        // Refresh stats after registration
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
        AddLog("🔄 Liveness reset");
    }

    [RelayCommand]
    private void ClearLog()
    {
        _dispatcher.Invoke(() => ActivityLog.Clear());
    }

    // ──────────────────────────────────────────────
    // Camera Frame Handler
    // ──────────────────────────────────────────────

    private async void OnFrameCaptured(object? sender, FrameEventArgs e)
    {
        try
        {
            // Process every Nth frame for face recognition
            if (e.ShouldProcess)
            {
                await _pipeline.ProcessFrameAsync(e.Frame);
            }

            // Draw overlays on EVERY frame (smooth video with persistent boxes)
            _pipeline.DrawOverlays(e.Frame);

            // Convert to frozen BitmapSource (thread-safe for WPF)
            var bitmap = WpfImageHelper.MatToFrozenBitmapSource(e.Frame);

            // Update UI on the dispatcher thread
            _dispatcher.BeginInvoke(() =>
            {
                CameraFrame = bitmap;
                FpsText = $"FPS: {_camera.CurrentFps:F1}";
            });
        }
        catch (Exception ex)
        {
            // Don't let frame processing errors crash the capture loop
            System.Diagnostics.Debug.WriteLine($"Frame error: {ex.Message}");
        }
        finally
        {
            // Dispose the frame Mat (it was cloned by CameraService)
            e.Frame.Dispose();
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
                    // Only log if this person wasn't in the previous results
                    AddLogIfNew($"✅ Recognized: {result.Person.Name} ({result.SimilarityText})");
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
            _dispatcher.Invoke(() =>
            {
                DatabaseText = $"Database: {stats.TotalPersons} persons, " +
                               $"{stats.TotalEmbeddings} samples";
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

    private string? _lastLogMessage;
    private void AddLogIfNew(string message)
    {
        if (message == _lastLogMessage) return;
        _lastLogMessage = message;
        AddLog(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _camera.FrameCaptured -= OnFrameCaptured;
        _camera.Stop();
    }
}

/// <summary>
/// View model wrapper for a single recognition result (for data binding).
/// </summary>
public class RecognitionResultViewModel
{
    public string Name { get; }
    public string Similarity { get; }
    public string Status { get; }
    public string StatusColor { get; }

    public RecognitionResultViewModel(RecognitionResult result)
    {
        Name = result.Person?.Name ?? "Unknown";
        Similarity = result.SimilarityText;

        if (!result.IsLive)
        {
            Status = "SPOOF";
            StatusColor = "#FF4444";
        }
        else if (result.IsHighConfidence)
        {
            Status = "HIGH";
            StatusColor = "#44BB44";
        }
        else if (result.IsRecognized)
        {
            Status = "MATCH";
            StatusColor = "#DDDD44";
        }
        else
        {
            Status = "UNKNOWN";
            StatusColor = "#FF8844";
        }
    }
}
