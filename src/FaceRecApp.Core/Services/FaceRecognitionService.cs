using FaceAiSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FaceRecApp.Core.Services;

/// <summary>
/// Generates face embeddings (512-dimensional vectors) using FaceAiSharp's ArcFace model.
/// 
/// What is a face embedding?
///   An embedding is a compact numerical representation of a face — a float[512] array.
///   Two photos of the same person produce similar embeddings (low cosine distance).
///   Two photos of different people produce different embeddings (high cosine distance).
/// 
/// How it works:
///   1. Take a detected face (from FaceDetectionService) with landmarks
///   2. Align the face using the 5 landmark points (straighten rotation, normalize position)
///   3. Feed the aligned 112×112 face image into the ArcFace neural network
///   4. The model outputs a 512-dimensional float vector
///   5. This vector is stored in SQL Server as VECTOR(512)
///   6. To match, we compute VECTOR_DISTANCE('cosine', stored, query)
/// 
/// ArcFace model details:
///   - Architecture: ResNet-based (lightweight variant in FaceAiSharp)
///   - Output: 512 floats, L2-normalized (unit vector)
///   - Accuracy: 99.77% on LFW benchmark
///   - Speed: ~50-100ms per face on CPU
/// 
/// Thread safety: NOT thread-safe. Use one instance per thread.
/// </summary>
public class FaceRecognitionService : IDisposable
{
    private readonly IFaceEmbeddingsGenerator _embedder;
    private bool _disposed;

    public FaceRecognitionService()
    {
        // Creates the ArcFace embedding generator with the bundled ONNX model.
        // Model is ~30MB, loaded into memory once.
        _embedder = FaceAiSharpBundleFactory.CreateFaceEmbeddingsGenerator();
    }

    // ──────────────────────────────────────────────
    // Core Embedding Generation
    // ──────────────────────────────────────────────

    /// <summary>
    /// Generate a 512-dimensional face embedding from a detected face.
    /// 
    /// IMPORTANT: This method needs BOTH the full image AND the detection result
    /// (with landmarks). The landmarks are used to align the face before embedding.
    /// 
    /// Steps performed internally:
    ///   1. Clone the image (alignment modifies pixels in-place)
    ///   2. Use 5-point landmarks to align the face (rotate + crop to 112×112)
    ///   3. Feed aligned face through ArcFace model
    ///   4. Return normalized 512-dim vector
    /// </summary>
    /// <param name="fullImage">The complete image (not cropped)</param>
    /// <param name="detectedFace">Detection result with landmarks from FaceDetectionService</param>
    /// <returns>float[512] embedding ready to store in SQL Server VECTOR(512)</returns>
    /// <exception cref="ArgumentException">If landmarks are missing from detection result</exception>
    public float[] GenerateEmbedding(Image<Rgb24> fullImage, FaceDetectorResult detectedFace)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (detectedFace.Landmarks == null || detectedFace.Landmarks.Count < 5)
        {
            throw new ArgumentException(
                "Face detection result must include landmarks (5-point). " +
                "Make sure you're using CreateFaceDetectorWithLandmarks(), not just CreateFaceDetector().");
        }

        // Clone the image because AlignFaceUsingLandmarks modifies it in-place.
        // We don't want to mess up the original image (it's used for display).
        using var alignedImage = fullImage.Clone();

        // Step 1: Align the face using landmark positions
        // This rotates and crops the image so the face is centered and straight.
        // The aligned image will be 112×112 pixels — the standard input for ArcFace.
        _embedder.AlignFaceUsingLandmarks(alignedImage, detectedFace.Landmarks);

        // Step 2: Generate the embedding vector
        // ArcFace processes the aligned face and outputs float[512]
        float[] embedding = _embedder.GenerateEmbedding(alignedImage);

        return embedding;
    }

    /// <summary>
    /// Generate embedding from an OpenCvSharp Mat frame.
    /// Convenience method that handles Mat → ImageSharp conversion.
    /// </summary>
    public float[] GenerateEmbedding(OpenCvSharp.Mat frame, FaceDetectorResult detectedFace)
    {
        using var image = Helpers.ImageConverter.MatToImageSharp(frame);
        return GenerateEmbedding(image, detectedFace);
    }

    // ──────────────────────────────────────────────
    // Batch Processing
    // ──────────────────────────────────────────────

    /// <summary>
    /// Generate embeddings for all detected faces in an image.
    /// 
    /// Returns a list of (FaceDetectorResult, float[]) pairs.
    /// Useful when processing a frame with multiple people.
    /// </summary>
    public IReadOnlyList<(FaceDetectorResult face, float[] embedding)> GenerateEmbeddings(
        Image<Rgb24> fullImage, IEnumerable<FaceDetectorResult> detectedFaces)
    {
        var results = new List<(FaceDetectorResult, float[])>();

        foreach (var face in detectedFaces)
        {
            if (face.Landmarks == null || face.Landmarks.Count < 5)
                continue; // Skip faces without landmarks

            try
            {
                var embedding = GenerateEmbedding(fullImage, face);
                results.Add((face, embedding));
            }
            catch (Exception)
            {
                // Skip faces that fail embedding generation
                // (e.g., face is too close to edge, alignment fails)
                continue;
            }
        }

        return results.AsReadOnly();
    }

    // ──────────────────────────────────────────────
    // Similarity Computation (CPU-side, for testing)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Compute cosine similarity between two embeddings.
    /// 
    /// Returns a value between -1 and 1:
    ///   1.0  = identical (same person, same photo)
    ///   0.42+ = likely same person (FaceAiSharp recommendation)
    ///   0.0  = unrelated
    ///  -1.0  = opposite (theoretically, rarely happens with faces)
    /// 
    /// NOTE: In production, use SQL Server's VECTOR_DISTANCE() instead.
    /// This method is for unit testing and debugging only.
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Embedding dimensions don't match: {a.Length} vs {b.Length}");

        float dot = 0f, magA = 0f, magB = 0f;

        // Use SIMD-friendly loop (compiler may auto-vectorize)
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        float denominator = MathF.Sqrt(magA) * MathF.Sqrt(magB);

        // Avoid division by zero
        if (denominator < float.Epsilon)
            return 0f;

        return dot / denominator;
    }

    /// <summary>
    /// Compute cosine distance (what SQL Server's VECTOR_DISTANCE returns).
    /// Distance = 1 - Similarity.
    /// 
    /// 0.0 = identical, 1.0 = completely different, 2.0 = opposite.
    /// </summary>
    public static float CosineDistance(float[] a, float[] b)
    {
        return 1f - CosineSimilarity(a, b);
    }

    /// <summary>
    /// Check if two embeddings belong to the same person.
    /// Uses the configured distance threshold from RecognitionSettings.
    /// </summary>
    public static bool IsSamePerson(float[] a, float[] b,
        float distanceThreshold = Entities.RecognitionSettings.DistanceThreshold)
    {
        return CosineDistance(a, b) <= distanceThreshold;
    }

    // ──────────────────────────────────────────────
    // Diagnostics
    // ──────────────────────────────────────────────

    /// <summary>
    /// Generate embedding with timing information.
    /// </summary>
    public (float[] embedding, TimeSpan elapsed) GenerateEmbeddingWithTiming(
        Image<Rgb24> fullImage, FaceDetectorResult detectedFace)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var embedding = GenerateEmbedding(fullImage, detectedFace);
        sw.Stop();
        return (embedding, sw.Elapsed);
    }

    /// <summary>
    /// Validate that an embedding has the correct dimensions and is normalized.
    /// Useful for debugging issues with the model.
    /// </summary>
    public static bool IsValidEmbedding(float[] embedding)
    {
        if (embedding == null || embedding.Length != Entities.RecognitionSettings.EmbeddingDimensions)
            return false;

        // Check it's not all zeros
        if (embedding.All(v => v == 0f))
            return false;

        // Check L2 norm is approximately 1.0 (ArcFace outputs normalized vectors)
        float norm = MathF.Sqrt(embedding.Sum(v => v * v));
        return norm > 0.9f && norm < 1.1f;
    }

    // ──────────────────────────────────────────────
    // Cleanup
    // ──────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_embedder is IDisposable disposable)
            disposable.Dispose();

        GC.SuppressFinalize(this);
    }
}
