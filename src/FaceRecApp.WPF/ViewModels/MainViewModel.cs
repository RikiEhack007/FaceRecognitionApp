using System.Collections.ObjectModel;
using System.Windows.Media;
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
            AddLog("Camera stopped");
        }
        else
        {
            bool success = _camera.Start(0);
            if (success)
            {
                IsCameraRunning = true;
                StatusText = "Camera running — detecting faces...";
                AddLog("Camera started");
            }
            else
            {
                StatusText = "Failed to open camera. Check connection.";
                AddLog("Camera failed to start");
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
        AddLog("Liveness reset");
    }

    [RelayCommand]
    private void ClearLog()
    {
        _dispatcher.Invoke(() => ActivityLog.Clear());
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
                        $"Recognized: {result.Person.Name} ({result.SimilarityText})",
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
    public string Similarity { get; }
    public string Status { get; }
    public string StatusColor { get; }

    public RecognitionResultViewModel(RecognitionResult result)
    {
        Name = result.Person?.Name ?? "Unknown";
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
