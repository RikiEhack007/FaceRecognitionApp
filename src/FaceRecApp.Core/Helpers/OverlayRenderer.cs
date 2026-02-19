using OpenCvSharp;
using FaceAiSharp;

namespace FaceRecApp.Core.Helpers;

/// <summary>
/// Draws face detection results as overlays on webcam frames.
/// 
/// Draws:
///   - Green box + name: recognized person (high confidence)
///   - Yellow box + name: recognized person (low confidence)
///   - Orange box + "Unknown": face detected but not recognized
///   - Red box + "FAKE": failed liveness check (possible spoofing)
/// 
/// All drawing uses OpenCvSharp functions directly on the Mat.
/// This is fast (~1ms) and doesn't require extra image conversion.
/// </summary>
public static class OverlayRenderer
{
    // Colors (BGR format for OpenCvSharp)
    private static readonly Scalar Green = new(0, 200, 0);
    private static readonly Scalar Yellow = new(0, 220, 220);
    private static readonly Scalar Orange = new(0, 165, 255);
    private static readonly Scalar Red = new(0, 0, 255);
    private static readonly Scalar White = new(255, 255, 255);
    private static readonly Scalar Black = new(0, 0, 0);

    /// <summary>
    /// Draw a single face detection result on the frame.
    /// </summary>
    /// <param name="frame">Webcam frame to draw on (modified in-place)</param>
    /// <param name="box">Face bounding box</param>
    /// <param name="label">Text label to display (name or "Unknown")</param>
    /// <param name="isRecognized">Was the face matched to someone?</param>
    /// <param name="isHighConfidence">High confidence match?</param>
    /// <param name="isLive">Passed liveness check?</param>
    public static void DrawFaceResult(
        Mat frame,
        Rect box,
        string label,
        bool isRecognized,
        bool isHighConfidence = false,
        bool isLive = true)
    {
        // Choose color based on status
        Scalar color;
        if (!isLive)
        {
            color = Red;
            label = "⚠ SPOOF DETECTED";
        }
        else if (isRecognized && isHighConfidence)
        {
            color = Green;
        }
        else if (isRecognized)
        {
            color = Yellow;
        }
        else
        {
            color = Orange;
        }

        // Draw bounding box
        Cv2.Rectangle(frame,
            new Point(box.X, box.Y),
            new Point(box.X + box.Width, box.Y + box.Height),
            color, 2);

        // Draw label background
        var fontScale = 0.55;
        var thickness = 1;
        var textSize = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex,
            fontScale, thickness, out var baseline);

        int labelY = Math.Max(box.Y - 5, textSize.Height + 10);

        // Background rectangle for text readability
        Cv2.Rectangle(frame,
            new Point(box.X, labelY - textSize.Height - 6),
            new Point(box.X + textSize.Width + 6, labelY + 4),
            color, -1); // Filled

        // Draw label text (white on colored background)
        Cv2.PutText(frame, label,
            new Point(box.X + 3, labelY - 2),
            HersheyFonts.HersheySimplex,
            fontScale, White, thickness);
    }

    /// <summary>
    /// Draw face landmarks (5 points) on the frame.
    /// Useful for debugging face alignment.
    /// </summary>
    public static void DrawLandmarks(Mat frame, FaceDetectorResult face)
    {
        if (face.Landmarks == null) return;

        foreach (var landmark in face.Landmarks)
        {
            Cv2.Circle(frame,
                new Point((int)landmark.X, (int)landmark.Y),
                2, Green, -1);
        }
    }

    /// <summary>
    /// Draw an FPS counter in the top-left corner.
    /// </summary>
    public static void DrawFps(Mat frame, double fps)
    {
        var text = $"FPS: {fps:F1}";
        Cv2.PutText(frame, text,
            new Point(10, 25),
            HersheyFonts.HersheySimplex,
            0.6, Green, 1);
    }

    /// <summary>
    /// Draw a status message at the bottom of the frame.
    /// </summary>
    public static void DrawStatus(Mat frame, string message, bool isError = false)
    {
        var color = isError ? Red : Green;
        var y = frame.Height - 15;
        Cv2.PutText(frame, message,
            new Point(10, y),
            HersheyFonts.HersheySimplex,
            0.5, color, 1);
    }

    /// <summary>
    /// Convert a FaceAiSharp RectangleF bounding box to an OpenCvSharp Rect.
    /// </summary>
    public static Rect ToOpenCvRect(SixLabors.ImageSharp.RectangleF box)
    {
        return new Rect(
            (int)box.X,
            (int)box.Y,
            (int)box.Width,
            (int)box.Height);
    }
}
