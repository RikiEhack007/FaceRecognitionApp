namespace FaceRecApp.Core.Services;

/// <summary>
/// Describes a discovered camera device.
/// Used for device enumeration and selection in the UI.
/// </summary>
public class CameraDeviceInfo
{
    /// <summary>
    /// OpenCV device index (0, 1, 2, ...).
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// Friendly display name from WMI or fallback label (e.g., "Camera 0").
    /// </summary>
    public string Name { get; init; } = "Unknown Camera";

    /// <summary>
    /// True if this device is a phone/virtual camera (Phone Link, DroidCam, etc.).
    /// Virtual cameras typically need DirectShow backend for reliable capture.
    /// </summary>
    public bool IsPhoneCamera { get; init; }

    /// <summary>
    /// PnP device ID from WMI (e.g., "USB\VID_xxxx&amp;PID_xxxx\...").
    /// May be empty if device was discovered via OpenCV probe only.
    /// </summary>
    public string DeviceId { get; init; } = "";

    public override string ToString() => Name;
}
