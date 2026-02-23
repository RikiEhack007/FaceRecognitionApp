using FaceRecApp.Core.Services;
using Xunit;

namespace FaceRecApp.Tests;

/// <summary>
/// Unit tests for CameraDeviceInfo and CameraService.AutoSelectDevice().
/// These run without hardware — no camera or WMI required.
/// </summary>
public class CameraDeviceTests
{
    [Fact]
    public void CameraDeviceInfo_DefaultValues_AreCorrect()
    {
        var device = new CameraDeviceInfo();

        Assert.Equal(0, device.Index);
        Assert.Equal("Unknown Camera", device.Name);
        Assert.False(device.IsPhoneCamera);
        Assert.Equal("", device.DeviceId);
    }

    [Fact]
    public void CameraDeviceInfo_ToString_ReturnsName()
    {
        var device = new CameraDeviceInfo { Name = "Logitech C920" };

        Assert.Equal("Logitech C920", device.ToString());
    }

    [Fact]
    public void AutoSelectDevice_EmptyList_ReturnsNull()
    {
        var camera = new CameraService();
        var result = camera.AutoSelectDevice([]);

        Assert.Null(result);
    }

    [Fact]
    public void AutoSelectDevice_PreferredName_MatchesCaseInsensitive()
    {
        var camera = new CameraService { PreferredDeviceName = "logitech" };
        var devices = new List<CameraDeviceInfo>
        {
            new() { Index = 0, Name = "Integrated Webcam" },
            new() { Index = 1, Name = "Logitech C920" },
            new() { Index = 2, Name = "Phone Link Camera" , IsPhoneCamera = true }
        };

        var result = camera.AutoSelectDevice(devices);

        Assert.NotNull(result);
        Assert.Equal(1, result.Index);
        Assert.Equal("Logitech C920", result.Name);
    }

    [Fact]
    public void AutoSelectDevice_PreferPhoneCamera_SelectsPhone()
    {
        var camera = new CameraService { PreferPhoneCamera = true };
        var devices = new List<CameraDeviceInfo>
        {
            new() { Index = 0, Name = "Integrated Webcam" },
            new() { Index = 1, Name = "Phone Link Camera", IsPhoneCamera = true }
        };

        var result = camera.AutoSelectDevice(devices);

        Assert.NotNull(result);
        Assert.Equal(1, result.Index);
        Assert.True(result.IsPhoneCamera);
    }

    [Fact]
    public void AutoSelectDevice_PreferPhoneCamera_FallsBackToPhysical()
    {
        var camera = new CameraService { PreferPhoneCamera = true };
        var devices = new List<CameraDeviceInfo>
        {
            new() { Index = 0, Name = "Integrated Webcam" },
            new() { Index = 1, Name = "Logitech C920" }
        };

        // No phone camera available — should fall back to first physical
        var result = camera.AutoSelectDevice(devices);

        Assert.NotNull(result);
        Assert.Equal(0, result.Index);
        Assert.False(result.IsPhoneCamera);
    }

    [Fact]
    public void AutoSelectDevice_NoPreferences_SelectsFirstPhysical()
    {
        var camera = new CameraService();
        var devices = new List<CameraDeviceInfo>
        {
            new() { Index = 0, Name = "OBS Virtual Camera", IsPhoneCamera = true },
            new() { Index = 1, Name = "Integrated Webcam" }
        };

        var result = camera.AutoSelectDevice(devices);

        Assert.NotNull(result);
        Assert.Equal(1, result.Index);
        Assert.Equal("Integrated Webcam", result.Name);
    }

    [Fact]
    public void AutoSelectDevice_OnlyVirtualCameras_SelectsFirst()
    {
        var camera = new CameraService();
        var devices = new List<CameraDeviceInfo>
        {
            new() { Index = 0, Name = "Phone Link Camera", IsPhoneCamera = true },
            new() { Index = 1, Name = "DroidCam Source", IsPhoneCamera = true }
        };

        // No physical cameras — should fall back to first device
        var result = camera.AutoSelectDevice(devices);

        Assert.NotNull(result);
        Assert.Equal(0, result.Index);
    }
}
