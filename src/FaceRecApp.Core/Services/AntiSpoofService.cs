using FaceRecApp.Core.Entities;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace FaceRecApp.Core.Services;

/// <summary>
/// ML-based anti-spoofing using MiniFASNetV2 ONNX model.
///
/// Classifies a face crop as Real vs. Spoof (phone screen, printout, video replay).
/// The model was trained on thousands of spoof samples and detects screen pixel patterns,
/// moire artifacts, printing textures, and lighting inconsistencies that hand-crafted
/// checks miss — especially modern OLED phone screens.
///
/// Model: MiniFASNetV2 from Silent-Face-Anti-Spoofing
/// Input: [1, 3, 80, 80] NCHW, BGR, float32, pixel values 0-255
/// Output: [1, 3] logits -> softmax -> class 1 = Real face
/// </summary>
public class AntiSpoofService : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private bool _disposed;

    /// <summary>
    /// Scale factor to expand the face bounding box before cropping.
    /// The model expects more context around the face (hair, background edges)
    /// to detect screen bezels and texture transitions.
    /// </summary>
    private const float CropScale = 2.7f;

    /// <summary>
    /// Model input resolution (80x80 pixels).
    /// </summary>
    private const int InputSize = 80;

    public AntiSpoofService()
    {
        var modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "MiniFASNetV2.onnx");

        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"Anti-spoofing model not found at: {modelPath}. " +
                "Ensure MiniFASNetV2.onnx is in the Models directory.", modelPath);

        var options = new SessionOptions();
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        // Use single thread — inference is fast (~5-20ms) and we don't want to starve other work
        options.InterOpNumThreads = 1;
        options.IntraOpNumThreads = 1;

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();

        Console.WriteLine($"[AntiSpoof] Model loaded: {modelPath}");
    }

    /// <summary>
    /// Run anti-spoofing prediction on a detected face.
    /// </summary>
    /// <param name="bgrFrame">Full camera frame (BGR Mat)</param>
    /// <param name="faceBox">Face bounding box from detector</param>
    /// <returns>Prediction with IsReal flag and confidence score</returns>
    public SpoofPrediction Predict(Mat bgrFrame, SixLabors.ImageSharp.RectangleF faceBox)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            // 1. Expand face box and crop from frame
            using var faceCrop = CropFaceWithScale(bgrFrame, faceBox, CropScale);

            // 2. Convert BGR HWC [80,80,3] to float CHW tensor [1,3,80,80]
            var tensor = MatToTensor(faceCrop);

            // 3. Run inference
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
            var output = results.First().AsEnumerable<float>().ToArray();

            // 4. Softmax on 3 logits, class 1 = Real
            var probabilities = Softmax(output);
            float realConfidence = probabilities[1];

            bool isReal = realConfidence >= RecognitionSettings.AntiSpoofThreshold;

            return new SpoofPrediction
            {
                IsReal = isReal,
                Confidence = realConfidence
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AntiSpoof] Prediction error: {ex.Message}");
            // On error, default to "real" to avoid blocking legitimate users
            return new SpoofPrediction { IsReal = true, Confidence = 0f };
        }
    }

    /// <summary>
    /// Expand face bounding box by a scale factor centered on the face,
    /// clamp to image bounds, crop, and resize to 80x80.
    /// </summary>
    private static Mat CropFaceWithScale(Mat frame, SixLabors.ImageSharp.RectangleF faceBox, float scale)
    {
        float centerX = faceBox.X + faceBox.Width / 2f;
        float centerY = faceBox.Y + faceBox.Height / 2f;

        // Use the larger dimension to create a square crop
        float size = Math.Max(faceBox.Width, faceBox.Height) * scale;
        float halfSize = size / 2f;

        // Expanded box (may extend outside image)
        int x1 = (int)(centerX - halfSize);
        int y1 = (int)(centerY - halfSize);
        int x2 = (int)(centerX + halfSize);
        int y2 = (int)(centerY + halfSize);

        // Clamp to image bounds
        x1 = Math.Max(0, x1);
        y1 = Math.Max(0, y1);
        x2 = Math.Min(frame.Width, x2);
        y2 = Math.Min(frame.Height, y2);

        int w = x2 - x1;
        int h = y2 - y1;

        if (w < 10 || h < 10)
        {
            // Face box too small — return a blank 80x80
            return new Mat(InputSize, InputSize, MatType.CV_8UC3, Scalar.All(0));
        }

        using var roi = new Mat(frame, new Rect(x1, y1, w, h));
        var resized = new Mat();
        Cv2.Resize(roi, resized, new Size(InputSize, InputSize));
        return resized;
    }

    /// <summary>
    /// Convert an 80x80 BGR Mat to a [1, 3, 80, 80] float32 CHW tensor.
    /// Pixel values stay in 0-255 range (no normalization — model expects raw values).
    /// </summary>
    private static DenseTensor<float> MatToTensor(Mat mat)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });

        // Direct pixel access via indexer — Mat is small (80x80) so this is fast
        for (int y = 0; y < InputSize; y++)
        {
            for (int x = 0; x < InputSize; x++)
            {
                var pixel = mat.At<Vec3b>(y, x);
                tensor[0, 0, y, x] = pixel.Item0; // B
                tensor[0, 1, y, x] = pixel.Item1; // G
                tensor[0, 2, y, x] = pixel.Item2; // R
            }
        }

        return tensor;
    }

    /// <summary>
    /// Compute softmax over an array of logits.
    /// </summary>
    private static float[] Softmax(float[] logits)
    {
        float max = logits.Max();
        var exps = logits.Select(l => MathF.Exp(l - max)).ToArray();
        float sum = exps.Sum();
        return exps.Select(e => e / sum).ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _session.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Result of ML anti-spoofing prediction.
/// </summary>
public class SpoofPrediction
{
    /// <summary>
    /// True if the model classifies this face as a real person (not a spoof).
    /// </summary>
    public bool IsReal { get; set; }

    /// <summary>
    /// Confidence score for the "Real" class (0.0 to 1.0).
    /// Higher = more confident the face is real.
    /// </summary>
    public float Confidence { get; set; }
}
