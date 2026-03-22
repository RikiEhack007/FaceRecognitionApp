using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FaceRecApp.Core.Services;
using FaceRecApp.WPF.Helpers;
using OpenCvSharp;

namespace FaceRecApp.WPF.ViewModels;

public partial class MainViewModel
{
    partial void OnSelectedCameraChanged(CameraDeviceInfo? value)
    {
        if (value == null)
            return;

        _preWarmCts?.Cancel();
        _preWarmCts?.Dispose();
        _preWarmCts = null;

        if (IsCameraRunning)
        {
            _ = SwitchCameraAsync(value);
        }
        else
        {
            _preWarmCts = new CancellationTokenSource();
            var token = _preWarmCts.Token;
            _ = Task.Run(() =>
            {
                if (!token.IsCancellationRequested)
                    _camera.PreWarm(value);
            }, token);
        }
    }

    private async Task SwitchCameraAsync(CameraDeviceInfo device)
    {
        if (_isSwitching) return;
        _isSwitching = true;
        try
        {
            StatusText = $"Switching to {device.Name}...";

            bool success = await Task.Run(() =>
            {
                _camera.Stop();
                return _camera.Start(device);
            });

            if (success)
            {
                IsCameraRunning = true;
                _pipeline.SkipAntiSpoof = device.IsPhoneCamera;
                StatusText = $"Switched to {device.Name}";
                AddLog($"Switched to camera: {device.Name}");
                if (device.IsPhoneCamera)
                    AddLog("Anti-spoof bypassed (virtual camera)");
            }
            else
            {
                IsCameraRunning = false;
                StatusText = $"Failed to open {device.Name}";
                AddLog($"Camera switch failed: {device.Name}");
            }
        }
        catch (Exception ex)
        {
            IsCameraRunning = false;
            StatusText = $"Camera switch error: {ex.Message}";
            AddLog($"Camera switch error: {ex.Message}");
        }
        finally
        {
            _isSwitching = false;
        }
    }

    [RelayCommand]
    private async Task ToggleCameraAsync()
    {
        if (IsCameraRunning)
        {
            _camera.Stop();
            _pipeline.SkipAntiSpoof = false;
            IsCameraRunning = false;
            StatusText = "Camera stopped.";
            FpsText = "FPS: --";
            AddLog("Camera stopped");
        }
        else
        {
            if (SelectedCamera == null)
            {
                StatusText = "No camera selected. Click Refresh to scan for devices.";
                AddLog("No camera selected");
                return;
            }

            var device = SelectedCamera;
            IsCameraRunning = true;
            StatusText = $"Opening {device.Name}...";
            AddLog($"Opening camera: {device.Name}");

            bool success = await Task.Run(() => _camera.Start(device));
            if (success)
            {
                _pipeline.SkipAntiSpoof = device.IsPhoneCamera;
                StatusText = $"Camera running ({device.Name}) -- detecting faces...";
                AddLog($"Camera started: {device.Name}");
                if (device.IsPhoneCamera)
                    AddLog("Anti-spoof bypassed (virtual camera)");
            }
            else
            {
                IsCameraRunning = false;
                StatusText = $"Failed to open {device.Name}. Check connection.";
                AddLog("Camera failed to start");
            }
        }
    }

    [RelayCommand]
    private Task RefreshCameraDevicesAsync()
    {
        try
        {
            var devices = _camera.GetAvailableDevices();

            CameraDevices.Clear();
            foreach (var device in devices)
                CameraDevices.Add(device);

            var selected = _camera.AutoSelectDevice(devices);
            SelectedCamera = selected;

            if (devices.Count == 0)
                AddLog("No cameras found");
            else
            {
                AddLog($"Found {devices.Count} camera(s)");
                if (selected != null)
                    AddLog($"Auto-selected: {selected.Name}");
            }
        }
        catch (Exception ex)
        {
            AddLog($"Camera refresh failed: {ex.Message}");
            StatusText = "Failed to enumerate cameras.";
        }
        return Task.CompletedTask;
    }

    private void OnFrameCaptured(object? sender, FrameEventArgs e)
    {
        bool ownershipTransferred = false;
        try
        {
            if (e.ShouldProcess && !_pipeline.IsProcessing)
            {
                var processingFrame = e.Frame.Clone();
                _ = Task.Run(async () =>
                {
                    try { await _pipeline.ProcessFrameAsync(processingFrame); }
                    finally { processingFrame.Dispose(); }
                });
            }

            try { _pipeline.DrawOverlays(e.Frame); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Overlay error: {ex.Message}"); }

            lock (_displayLock)
            {
                _latestDisplayFrame?.Dispose();
                _latestDisplayFrame = e.Frame;
            }
            ownershipTransferred = true;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Frame error: {ex.Message}"); }
        finally { if (!ownershipTransferred) e.Frame.Dispose(); }
    }

    private void OnRender(object? sender, EventArgs e)
    {
        Mat? frame;
        lock (_displayLock) { frame = _latestDisplayFrame; _latestDisplayFrame = null; }
        if (frame == null) return;

        try
        {
            if (_writeableBitmap == null ||
                _writeableBitmap.PixelWidth != frame.Width ||
                _writeableBitmap.PixelHeight != frame.Height)
            {
                _writeableBitmap = WpfImageHelper.CreateWriteableBitmap(frame);
                CameraFrame = _writeableBitmap;
            }
            else
            {
                WpfImageHelper.UpdateWriteableBitmap(frame, _writeableBitmap);
            }
            FpsText = $"FPS: {_camera.CurrentFps:F1}";
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Render] Error: {ex.Message}"); }
        finally { frame.Dispose(); }
    }

    private void OnResultsUpdated(object? sender, IReadOnlyList<RecognitionResult> results)
    {
        _dispatcher.BeginInvoke(() =>
        {
            TimingText = $"Detect: {_pipeline.LastDetectionTime.TotalMilliseconds:F0}ms | " +
                         $"Embed: {_pipeline.LastEmbeddingTime.TotalMilliseconds:F0}ms | " +
                         $"Search: {_pipeline.LastSearchTime.TotalMilliseconds:F0}ms | " +
                         $"Total: {_pipeline.LastTotalTime.TotalMilliseconds:F0}ms";

            LivenessText = _pipeline.LivenessStatusText;

            CurrentResults.Clear();
            foreach (var result in results)
            {
                CurrentResults.Add(new RecognitionResultViewModel(result));
                if (result.IsRecognized && result.Patient != null)
                {
                    AddLogIfNew(
                        $"Recognized: {result.Patient.FullName} ({result.SimilarityText})",
                        $"recognized_{result.Patient.IDCard}");
                }
            }

            if (results.Count == 0)
                StatusText = "No faces detected";
            else
            {
                var recognized = results.Count(r => r.IsRecognized);
                StatusText = $"Detected {results.Count} face(s), {recognized} recognized";
            }
        });
    }
}
