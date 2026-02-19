using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.IO;

namespace FaceRecApp.Core.Helpers;

/// <summary>
/// Converts images between OpenCvSharp Mat and ImageSharp Image&lt;Rgb24&gt;.
/// 
/// These two formats are used throughout the project:
///   - OpenCvSharp Mat:  webcam capture, drawing overlays
///   - ImageSharp Image: FaceAiSharp processing (detection + embedding)
/// 
/// WPF-specific conversions (BitmapSource) are in FaceRecApp.WPF/Helpers/WpfImageHelper.cs
/// because they require Windows-specific assemblies.
/// 
/// Performance: ~5-10ms per conversion via JPEG encode/decode.
/// </summary>
public static class ImageConverter
{
    // ──────────────────────────────────────────────
    // Mat ↔ ImageSharp conversions
    // ──────────────────────────────────────────────

    /// <summary>
    /// Convert OpenCvSharp Mat (BGR) → ImageSharp Image&lt;Rgb24&gt; (RGB).
    /// Used every frame when passing webcam data to FaceAiSharp.
    /// </summary>
    public static Image<Rgb24> MatToImageSharp(Mat mat)
    {
        byte[] jpegBytes = mat.ToBytes(".jpg",
            new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
        return Image.Load<Rgb24>(jpegBytes);
    }

    /// <summary>
    /// Convert ImageSharp Image&lt;Rgb24&gt; → OpenCvSharp Mat.
    /// Used when needing to draw on the image with OpenCvSharp after AI processing.
    /// </summary>
    public static Mat ImageSharpToMat(Image<Rgb24> image)
    {
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = 90 });
        ms.Position = 0;
        return Mat.FromStream(ms, ImreadModes.Color);
    }

    // ──────────────────────────────────────────────
    // Face Thumbnail
    // ──────────────────────────────────────────────

    /// <summary>
    /// Crop a detected face region, resize to thumbnail, return as JPEG bytes.
    /// Stored in database for display in the management UI.
    /// Output: 112x112 pixels, ~5-10 KB.
    /// </summary>
    public static byte[] CropFaceThumbnail(
        Image<Rgb24> image,
        SixLabors.ImageSharp.RectangleF faceBox,
        int size = 112)
    {
        // Clamp bounding box to image boundaries
        int x = Math.Max(0, (int)faceBox.X);
        int y = Math.Max(0, (int)faceBox.Y);
        int width = Math.Min((int)faceBox.Width, image.Width - x);
        int height = Math.Min((int)faceBox.Height, image.Height - y);

        if (width < 10 || height < 10)
            throw new ArgumentException("Face bounding box is too small to crop");

        var cropRect = new SixLabors.ImageSharp.Rectangle(x, y, width, height);

        using var cropped = image.Clone(ctx =>
            ctx.Crop(cropRect)
               .Resize(new ResizeOptions
               {
                   Size = new SixLabors.ImageSharp.Size(size, size),
                   Mode = ResizeMode.Stretch
               }));

        using var ms = new MemoryStream();
        cropped.SaveAsJpeg(ms, new JpegEncoder { Quality = 80 });
        return ms.ToArray();
    }

    // ──────────────────────────────────────────────
    // Frame Quality Check
    // ──────────────────────────────────────────────

    /// <summary>
    /// Quick check if a webcam frame is usable (not too dark, not too blurry).
    /// Saves processing time by skipping bad frames.
    /// </summary>
    public static bool IsFrameUsable(Mat mat)
    {
        if (mat.Empty()) return false;

        using var gray = new Mat();
        Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);

        // Check brightness
        var mean = Cv2.Mean(gray);
        if (mean.Val0 < 40 || mean.Val0 > 220) return false;

        // Check blur (Laplacian variance)
        using var laplacian = new Mat();
        Cv2.Laplacian(gray, laplacian, MatType.CV_64F);
        Cv2.MeanStdDev(laplacian, out _, out var stddev);
        double variance = stddev.Val0 * stddev.Val0;

        return variance >= 50;
    }
}
