using System.Windows;
using System.Windows.Threading;
using FaceRecApp.Core.Services;
using FaceRecApp.WPF.Helpers;
using Microsoft.Extensions.DependencyInjection;
using OpenCvSharp;

namespace FaceRecApp.WPF.Views;

/// <summary>
/// Registration window — captures a face from the camera and registers a new person.
/// 
/// Uses the existing CameraService if it's running, or captures a snapshot directly.
/// Shows a live preview and allows the user to capture when ready.
/// </summary>
public partial class RegisterWindow : System.Windows.Window
{
    private readonly CameraService _camera;
    private readonly RecognitionPipeline _pipeline;
    private readonly DispatcherTimer _previewTimer;
    private bool _isRegistering;

    public RegisterWindow()
    {
        InitializeComponent();

        _camera = App.Services.GetRequiredService<CameraService>();
        _pipeline = App.Services.GetRequiredService<RecognitionPipeline>();

        // Update preview from camera at ~15fps
        _previewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(66)
        };
        _previewTimer.Tick += OnPreviewTick;

        Loaded += (_, _) =>
        {
            if (_camera.IsRunning)
            {
                _previewTimer.Start();
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                PreviewPlaceholder.Text = "Camera is not running.\nStart camera from the main window first.";
            }
        };

        Closing += (_, _) => _previewTimer.Stop();
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        if (!_camera.IsRunning) return;

        try
        {
            using var frame = _camera.CaptureSnapshot();
            if (frame == null) return;

            var bitmap = WpfImageHelper.MatToFrozenBitmapSource(frame);
            PreviewImage.Source = bitmap;
        }
        catch { }
    }

    private async void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (_isRegistering) return;

        string name = NameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusLabel.Text = "Please enter a name.";
            StatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF3, 0x87, 0x87));
            NameInput.Focus();
            return;
        }

        if (!_camera.IsRunning)
        {
            StatusLabel.Text = "Camera is not running. Start it from the main window.";
            StatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF3, 0x87, 0x87));
            return;
        }

        _isRegistering = true;
        CaptureButton.IsEnabled = false;
        StatusLabel.Text = "Capturing face... Please hold still.";
        StatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x89, 0xB4, 0xFA));

        try
        {
            // Capture a snapshot
            using var frame = _camera.CaptureSnapshot();
            if (frame == null)
            {
                StatusLabel.Text = "Failed to capture frame. Try again.";
                return;
            }

            string? notes = string.IsNullOrWhiteSpace(NotesInput.Text) ? null : NotesInput.Text.Trim();

            // Register through the pipeline
            var result = await _pipeline.RegisterFromFrameAsync(frame, name, notes);

            if (result.Success)
            {
                StatusLabel.Text = $"Successfully registered '{result.Person!.Name}'!";
                StatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1));

                // Auto-close after brief delay
                await Task.Delay(1500);
                DialogResult = true;
                Close();
            }
            else
            {
                StatusLabel.Text = result.Error ?? "Registration failed.";
                StatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xF3, 0x87, 0x87));
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error: {ex.Message}";
            StatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF3, 0x87, 0x87));
        }
        finally
        {
            _isRegistering = false;
            CaptureButton.IsEnabled = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
