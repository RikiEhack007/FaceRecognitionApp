using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System.IO;
using System.Windows.Media.Imaging;

namespace FaceRecApp.WPF.Helpers;

/// <summary>
/// WPF-specific image conversions.
/// These require Windows assemblies (System.Windows.Media.Imaging)
/// so they live in the WPF project, not Core.
/// 
/// Core/Helpers/ImageConverter.cs handles: Mat ↔ ImageSharp
/// This class handles: Mat → BitmapSource, byte[] → BitmapImage
/// </summary>
public static class WpfImageHelper
{
    /// <summary>
    /// Convert OpenCvSharp Mat → WPF BitmapSource for display in Image control.
    /// Must be called on the UI thread.
    /// </summary>
    public static BitmapSource MatToBitmapSource(Mat mat)
    {
        return BitmapSourceConverter.ToBitmapSource(mat);
    }

    /// <summary>
    /// Convert Mat to a frozen (thread-safe) BitmapSource.
    /// 
    /// Use this when generating the image on a background thread:
    ///   var bitmap = WpfImageHelper.MatToFrozenBitmapSource(frame);
    ///   Dispatcher.Invoke(() => myImage.Source = bitmap);  // safe!
    /// </summary>
    public static BitmapSource MatToFrozenBitmapSource(Mat mat)
    {
        var bitmapSource = BitmapSourceConverter.ToBitmapSource(mat);
        bitmapSource.Freeze();
        return bitmapSource;
    }

    // ──────────────────────────────────────────────
    // WriteableBitmap (real-time video display)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Create a new WriteableBitmap from a Mat.
    /// Call once to initialize, then use UpdateWriteableBitmap for subsequent frames.
    /// Must be called on the UI thread.
    /// </summary>
    public static WriteableBitmap CreateWriteableBitmap(Mat mat)
    {
        return WriteableBitmapConverter.ToWriteableBitmap(mat);
    }

    /// <summary>
    /// Update an existing WriteableBitmap in-place from a Mat.
    /// No allocation — copies pixels directly via Lock/memcpy/Unlock.
    /// ~1-2ms for 640x480. Must be called on the UI thread.
    /// </summary>
    public static void UpdateWriteableBitmap(Mat mat, WriteableBitmap bitmap)
    {
        WriteableBitmapConverter.ToWriteableBitmap(mat, bitmap);
    }

    // ──────────────────────────────────────────────
    // Thumbnails
    // ──────────────────────────────────────────────

    /// <summary>
    /// Convert JPEG byte array → WPF BitmapImage.
    /// Used for displaying face thumbnails stored in the database.
    /// </summary>
    public static BitmapImage? BytesToBitmapImage(byte[]? jpegBytes)
    {
        if (jpegBytes == null || jpegBytes.Length == 0)
            return null;

        var bitmap = new BitmapImage();
        using var ms = new MemoryStream(jpegBytes);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze(); // Thread-safe
        return bitmap;
    }
}
