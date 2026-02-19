using FaceAiSharp;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using FaceRecApp.Core.Helpers;

namespace FaceRecApp.Core.Services;

/// <summary>
/// Detects faces in images using FaceAiSharp's SCRFD model.
/// 
/// What it does:
///   1. Takes a camera frame (Mat or Image&lt;Rgb24&gt;)
///   2. Runs the SCRFD (Sample and Computation Redistribution for Face Detection) model
///   3. Returns bounding boxes + 5-point facial landmarks for each detected face
/// 
/// The landmarks (left eye, right eye, nose, left mouth corner, right mouth corner)
/// are critical for the next step: face alignment before generating embeddings.
/// 
/// Performance:
///   - SCRFD is one of the fastest face detectors available
///   - Typical: 20-50ms per frame on a modern CPU
///   - Can detect multiple faces simultaneously
///   - Works on various face sizes (from ~20px to full-frame)
/// 
/// Thread safety:
///   - NOT thread-safe. Create one instance per thread, or use locking.
///   - For our PoC, we process on a single background thread, so this is fine.
/// 
/// Lifecycle:
///   - Create once at app startup
///   - Dispose when app shuts down
///   - The ONNX model stays loaded in memory (~10 MB)
/// </summary>
public class FaceDetectionService : IDisposable
{
    private readonly IFaceDetectorWithLandmarks _detector;
    private bool _disposed;

    public FaceDetectionService()
    {
        // FaceAiSharpBundleFactory creates a detector with the bundled SCRFD ONNX model.
        // The model is embedded in the FaceAiSharp.Bundle NuGet package — no manual download.
        //
        // Detection capabilities:
        //   - Bounding box (x, y, width, height) for each face
        //   - Confidence score (0.0 - 1.0)
        //   - 5 facial landmarks (left eye, right eye, nose, mouth corners)
        //   - Works at various scales (near and far faces)
        _detector = FaceAiSharpBundleFactory.CreateFaceDetectorWithLandmarks();
    }

    // ──────────────────────────────────────────────
    // Core Detection Methods
    // ──────────────────────────────────────────────

    /// <summary>
    /// Detect all faces in an ImageSharp image.
    /// 
    /// Returns a list of FaceDetectorResult, each containing:
    ///   - Box: bounding rectangle of the face
    ///   - Confidence: detection confidence (0.0 - 1.0)
    ///   - Landmarks: 5-point facial landmarks (nullable)
    /// </summary>
    public IReadOnlyList<FaceDetectorResult> DetectFaces(Image<Rgb24> image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var results = _detector.DetectFaces(image);
        return results.ToList().AsReadOnly();
    }

    /// <summary>
    /// Detect faces from an OpenCvSharp Mat (webcam frame).
    /// 
    /// Converts Mat → Image&lt;Rgb24&gt; internally, then runs detection.
    /// This is the method you'll call most often from the camera pipeline.
    /// </summary>
    /// <param name="frame">BGR Mat from OpenCvSharp VideoCapture</param>
    /// <returns>List of detected faces with bounding boxes and landmarks</returns>
    public IReadOnlyList<FaceDetectorResult> DetectFaces(Mat frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var image = ImageConverter.MatToImageSharp(frame);
        return DetectFaces(image);
    }

    // ──────────────────────────────────────────────
    // Convenience Methods
    // ──────────────────────────────────────────────

    /// <summary>
    /// Detect faces and return only those above a confidence threshold.
    /// 
    /// FaceAiSharp already filters low-confidence detections,
    /// but this adds an extra layer for stricter filtering.
    /// </summary>
    public IReadOnlyList<FaceDetectorResult> DetectFaces(
        Image<Rgb24> image, float minConfidence = 0.7f)
    {
        var all = DetectFaces(image);
        return all.Where(f => f.Confidence >= minConfidence).ToList().AsReadOnly();
    }

    /// <summary>
    /// Detect the single largest face in the image.
    /// 
    /// Used during registration when we expect exactly one person.
    /// Returns the face with the largest bounding box area.
    /// Returns null if no faces detected.
    /// </summary>
    public FaceDetectorResult? DetectLargestFace(Image<Rgb24> image)
    {
        var faces = DetectFaces(image);
        if (faces.Count == 0) return null;

        return faces
            .OrderByDescending(f => f.Box.Width * f.Box.Height)
            .First();
    }

    /// <summary>
    /// Detect the largest face from a Mat frame.
    /// </summary>
    public FaceDetectorResult? DetectLargestFace(Mat frame)
    {
        using var image = ImageConverter.MatToImageSharp(frame);
        return DetectLargestFace(image);
    }

    /// <summary>
    /// Quick check: is there at least one face in the frame?
    /// Slightly faster than getting full results if you only need a boolean.
    /// </summary>
    public bool HasFace(Mat frame)
    {
        using var image = ImageConverter.MatToImageSharp(frame);
        return _detector.DetectFaces(image).Any();
    }

    // ──────────────────────────────────────────────
    // Diagnostics
    // ──────────────────────────────────────────────

    /// <summary>
    /// Detect faces and return timing information.
    /// Useful for performance profiling.
    /// </summary>
    public (IReadOnlyList<FaceDetectorResult> faces, TimeSpan elapsed) DetectFacesWithTiming(Mat frame)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var faces = DetectFaces(frame);
        sw.Stop();
        return (faces, sw.Elapsed);
    }

    // ──────────────────────────────────────────────
    // Cleanup
    // ──────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // FaceAiSharp's detector may implement IDisposable
        // to release the ONNX Runtime session and GPU memory
        if (_detector is IDisposable disposable)
            disposable.Dispose();

        GC.SuppressFinalize(this);
    }
}
