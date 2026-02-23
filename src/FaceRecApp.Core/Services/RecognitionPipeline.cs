using FaceAiSharp;
using FaceRecApp.Core.Entities;
using FaceRecApp.Core.Helpers;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Diagnostics;

namespace FaceRecApp.Core.Services;

/// <summary>
/// The main orchestrator that ties all services together.
/// 
/// Pipeline flow for each processed frame:
///   1. Camera delivers a Mat frame
///   2. Convert Mat → ImageSharp Image
///   3. FaceDetectionService detects face(s) + landmarks
///   4. FaceRecognitionService generates float[512] embedding(s)
///   5. FaceRepository.FindClosestMatch() runs SQL VECTOR_DISTANCE query
///   6. LivenessService checks for blink
///   7. Return RecognitionResult(s) to the UI
/// 
/// Frame skipping:
///   Camera runs at ~30fps, but AI processing is ~5fps.
///   We process every 6th frame for recognition.
///   All frames are displayed in the UI (smooth video).
///   Face boxes from the last processed frame are drawn on all frames.
/// 
/// Registration flow:
///   1. User clicks "Register" button
///   2. Pipeline enters registration mode
///   3. Captures N face samples (different moments = slight angle variations)
///   4. Generates embeddings for each sample
///   5. Stores person + embeddings in SQL Server
///   6. Returns to recognition mode
/// </summary>
public class RecognitionPipeline : IDisposable
{
    private readonly FaceDetectionService _detector;
    private readonly FaceRecognitionService _recognizer;
    private readonly LivenessService _liveness;
    private readonly FaceRepository _repository;
    private readonly AntiSpoofService _antiSpoof;
    private bool _disposed;

    /// <summary>
    /// When true, skip ML anti-spoofing checks (treat all faces as real).
    /// Enable this for virtual/phone cameras (Phone Link, DroidCam, etc.)
    /// which trigger false positives because the model sees screen artifacts.
    /// </summary>
    public bool SkipAntiSpoof { get; set; }

    // ─── Last processed results (displayed on all frames) ───
    private readonly object _resultsLock = new();
    private List<RecognitionResult> _lastResults = new();

    /// <summary>
    /// Most recent recognition results.
    /// Updated every time a frame is processed (~5fps).
    /// Thread-safe via locking.
    /// </summary>
    public IReadOnlyList<RecognitionResult> LastResults
    {
        get
        {
            lock (_resultsLock)
                return _lastResults.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Fired after each processed frame with updated results.
    /// Subscribe to this for UI updates.
    /// WARNING: Fires on a background thread — use Dispatcher.Invoke for WPF.
    /// </summary>
    public event EventHandler<IReadOnlyList<RecognitionResult>>? ResultsUpdated;

    /// <summary>
    /// Fired when an error occurs during processing.
    /// </summary>
    public event EventHandler<string>? ProcessingError;

    /// <summary>
    /// Atomic flag to prevent overlapping processing.
    /// 0 = idle, 1 = processing. Uses Interlocked for thread safety.
    /// </summary>
    private int _isProcessing;

    /// <summary>
    /// Is the pipeline currently processing a frame?
    /// </summary>
    public bool IsProcessing => _isProcessing != 0;

    // ─── Performance tracking ───
    public TimeSpan LastDetectionTime { get; private set; }
    public TimeSpan LastEmbeddingTime { get; private set; }
    public TimeSpan LastSearchTime { get; private set; }
    public TimeSpan LastTotalTime { get; private set; }

    public RecognitionPipeline(
        FaceDetectionService detector,
        FaceRecognitionService recognizer,
        LivenessService liveness,
        FaceRepository repository,
        AntiSpoofService antiSpoof)
    {
        _detector = detector;
        _recognizer = recognizer;
        _liveness = liveness;
        _repository = repository;
        _antiSpoof = antiSpoof;
    }

    // ══════════════════════════════════════════════
    //  RECOGNITION MODE — Process a camera frame
    // ══════════════════════════════════════════════

    /// <summary>
    /// Process a single camera frame through the full pipeline.
    /// 
    /// Call this for every Nth frame (based on ProcessEveryNFrames setting).
    /// Returns recognition results for all detected faces.
    /// </summary>
    /// <param name="frame">Webcam frame (BGR Mat)</param>
    /// <returns>List of recognition results (one per detected face)</returns>
    public async Task<IReadOnlyList<RecognitionResult>> ProcessFrameAsync(Mat frame)
    {
        // Atomic check-and-set: only one thread can enter at a time.
        // CompareExchange returns the OLD value; if it was 0, we set it to 1 and proceed.
        // If it was already 1, another thread is processing — skip this frame.
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
        {
            Console.WriteLine($"[Pipeline] SKIPPED — already processing");
            return LastResults;
        }

        var totalSw = Stopwatch.StartNew();
        var results = new List<RecognitionResult>();

        try
        {
            // Skip poor quality frames
            if (!ImageConverter.IsFrameUsable(frame))
            {
                Console.WriteLine($"[Pipeline] Frame skipped — not usable");
                return results;
            }

            // Step 1: Convert to ImageSharp (needed by FaceAiSharp)
            var convertSw = Stopwatch.StartNew();
            using var image = ImageConverter.MatToImageSharp(frame);
            convertSw.Stop();
            Console.WriteLine($"[Pipeline] Convert: {convertSw.ElapsedMilliseconds}ms");

            // Step 2: Detect faces
            var detectionSw = Stopwatch.StartNew();
            var faces = _detector.DetectFaces(image);
            detectionSw.Stop();
            LastDetectionTime = detectionSw.Elapsed;
            Console.WriteLine($"[Pipeline] Detect: {detectionSw.ElapsedMilliseconds}ms, found {faces.Count} face(s)");

            if (faces.Count == 0)
            {
                // No faces — notify liveness service for auto-reset
                _liveness.OnNoFaceDetected();

                // Clear previous results
                lock (_resultsLock)
                    _lastResults = results;

                ResultsUpdated?.Invoke(this, results);
                return results;
            }

            // Find the largest face (by bounding box area) — this is the primary face
            // for liveness tracking. Only the primary face gets full liveness processing
            // (blink tracking, micro-movement, identity tracking). All other faces are
            // treated as secondary — they get per-face spoof detection but no liveness state.
            // This prevents phone photo faces from corrupting the real person's liveness data.
            int primaryFaceIndex = 0;
            float maxArea = 0;
            for (int i = 0; i < faces.Count; i++)
            {
                float area = faces[i].Box.Width * faces[i].Box.Height;
                if (area > maxArea)
                {
                    maxArea = area;
                    primaryFaceIndex = i;
                }
            }

            Console.WriteLine($"[Pipeline] Primary face: index={primaryFaceIndex} (area={maxArea:F0})");

            // Step 3 & 4: For each detected face → generate embedding → search database
            for (int i = 0; i < faces.Count; i++)
            {
                var face = faces[i];
                bool isPrimary = i == primaryFaceIndex;

                var result = new RecognitionResult
                {
                    FaceBox = face.Box,
                    DetectionTime = detectionSw.Elapsed
                };

                try
                {
                    // Generate embedding
                    var embedSw = Stopwatch.StartNew();
                    var embedding = _recognizer.GenerateEmbedding(image, face);
                    embedSw.Stop();
                    result.EmbeddingTime = embedSw.Elapsed;
                    result.Embedding = embedding;
                    Console.WriteLine($"[Pipeline]   Face[{i}] Embed: {embedSw.ElapsedMilliseconds}ms {(isPrimary ? "(primary)" : "(secondary)")}");

                    // Search database
                    var searchSw = Stopwatch.StartNew();
                    var match = await _repository.FindClosestMatchAsync(embedding);
                    searchSw.Stop();
                    result.SearchTime = searchSw.Elapsed;
                    LastSearchTime = searchSw.Elapsed;
                    Console.WriteLine($"[Pipeline]   Face[{i}] Search: {searchSw.ElapsedMilliseconds}ms");

                    if (match != null)
                    {
                        result.Distance = match.Distance;
                        result.IsRecognized = match.IsMatch;
                        result.Person = match.IsMatch ? match.Person : null;
                    }

                    // Per-face ML anti-spoofing (skipped for virtual/phone cameras)
                    if (SkipAntiSpoof)
                    {
                        result.IsSpoofDetected = false;
                        Console.WriteLine($"[Pipeline]   Face[{i}] Spoof: SKIPPED (virtual camera)");
                    }
                    else
                    {
                        var spoofSw = Stopwatch.StartNew();
                        var spoofResult = _antiSpoof.Predict(frame, face.Box);
                        result.IsSpoofDetected = !spoofResult.IsReal;
                        spoofSw.Stop();
                        Console.WriteLine($"[Pipeline]   Face[{i}] Spoof: {spoofSw.ElapsedMilliseconds}ms → {(spoofResult.IsReal ? "REAL" : "SPOOF")} (conf={spoofResult.Confidence:F3})");
                    }

                    // Liveness check — only run on the primary (largest) face.
                    // Secondary faces never get liveness confirmation to prevent
                    // phone photos from interfering with real-person blink tracking.
                    if (isPrimary && !result.IsSpoofDetected)
                    {
                        var livenessSw = Stopwatch.StartNew();
                        result.IsLive = _liveness.ProcessFrame(image, face, embedding, frame);
                        livenessSw.Stop();
                        Console.WriteLine($"[Pipeline]   Face[{i}] Liveness: {livenessSw.ElapsedMilliseconds}ms → {(result.IsLive ? "LIVE" : "PENDING")}");
                    }
                    else if (result.IsSpoofDetected)
                    {
                        // Spoof-detected faces are never live
                        result.IsLive = false;
                        Console.WriteLine($"[Pipeline]   Face[{i}] Liveness: SKIPPED (spoof detected)");
                    }
                    else
                    {
                        // Secondary (non-primary) faces: not tracked for liveness
                        result.IsLive = false;
                        Console.WriteLine($"[Pipeline]   Face[{i}] Liveness: SKIPPED (secondary face)");
                    }
                }
                catch (Exception ex)
                {
                    ProcessingError?.Invoke(this, $"Face processing error: {ex.Message}");
                }

                results.Add(result);
            }

            // Log recognitions (fire-and-forget, don't block the pipeline)
            _ = Task.Run(async () =>
            {
                foreach (var result in results.Where(r => r.IsRecognized || r.Embedding != null))
                {
                    try
                    {
                        await _repository.LogRecognitionAsync(
                            result.Person?.Id,
                            result.Distance,
                            result.IsRecognized,
                            result.IsLive);
                    }
                    catch (Exception logEx)
                    {
                        Console.WriteLine($"[Pipeline] LogRecognition error: {logEx.GetBaseException().Message}");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            ProcessingError?.Invoke(this, $"Pipeline error: {ex.Message}");
        }
        finally
        {
            totalSw.Stop();
            LastTotalTime = totalSw.Elapsed;
            LastEmbeddingTime = results.FirstOrDefault()?.EmbeddingTime ?? TimeSpan.Zero;

            Console.WriteLine($"[Pipeline] TOTAL: {totalSw.ElapsedMilliseconds}ms, {results.Count} result(s)");

            // Update shared results
            lock (_resultsLock)
                _lastResults = results;

            ResultsUpdated?.Invoke(this, results);

            // Release the processing lock (atomic)
            Interlocked.Exchange(ref _isProcessing, 0);
        }

        return results;
    }

    // ══════════════════════════════════════════════
    //  REGISTRATION MODE — Register a new person
    // ══════════════════════════════════════════════

    /// <summary>
    /// Register a new person from a single camera frame.
    /// Detects the largest face, generates embedding, stores in database.
    /// </summary>
    /// <param name="frame">Camera frame containing the person's face</param>
    /// <param name="name">Person's name</param>
    /// <param name="notes">Optional notes</param>
    /// <returns>Registration result</returns>
    public async Task<RegistrationResult> RegisterFromFrameAsync(
        Mat frame, string name, string? notes = null)
    {
        var result = new RegistrationResult();

        try
        {
            using var image = ImageConverter.MatToImageSharp(frame);

            // Detect the largest face
            var face = _detector.DetectLargestFace(image);
            if (face == null)
            {
                result.Error = "No face detected in the frame. Please ensure your face is clearly visible.";
                return result;
            }

            // Generate embedding
            var embedding = _recognizer.GenerateEmbedding(image, face.Value);

            if (!FaceRecognitionService.IsValidEmbedding(embedding))
            {
                result.Error = "Failed to generate a valid face embedding. Try again with better lighting.";
                return result;
            }

            // Check if this face already exists
            var existingMatch = await _repository.FindClosestMatchAsync(embedding);
            if (existingMatch != null && existingMatch.IsMatch)
            {
                result.Error = $"This face appears to already be registered as '{existingMatch.Person.Name}' " +
                               $"(similarity: {existingMatch.SimilarityText}). " +
                               "Use 'Add Sample' to add more photos to an existing person.";
                result.ExistingPerson = existingMatch.Person;
                return result;
            }

            // Create thumbnail
            byte[]? thumbnail = null;
            try
            {
                thumbnail = ImageConverter.CropFaceThumbnail(image, face.Value.Box);
            }
            catch { /* Thumbnail failure is not critical */ }

            // Store in database
            var person = await _repository.RegisterPersonAsync(
                name, embedding, thumbnail, notes);

            result.Success = true;
            result.Person = person;
        }
        catch (Exception ex)
        {
            result.Error = $"Registration failed: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Add another face sample to an existing person.
    /// </summary>
    public async Task<bool> AddFaceSampleAsync(Mat frame, int personId, string? angle = null)
    {
        try
        {
            using var image = ImageConverter.MatToImageSharp(frame);

            var face = _detector.DetectLargestFace(image);
            if (face == null) return false;

            var embedding = _recognizer.GenerateEmbedding(image, face.Value);
            if (!FaceRecognitionService.IsValidEmbedding(embedding)) return false;

            byte[]? thumbnail = null;
            try { thumbnail = ImageConverter.CropFaceThumbnail(image, face.Value.Box); }
            catch { }

            await _repository.AddFaceSampleAsync(personId, embedding, thumbnail, angle);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ══════════════════════════════════════════════
    //  OVERLAY DRAWING — Draw results on frame
    // ══════════════════════════════════════════════

    /// <summary>
    /// Draw recognition results (face boxes + labels) on a camera frame.
    /// Call this for EVERY frame (even non-processed ones) to keep overlays visible.
    /// Uses the last processed results.
    /// </summary>
    public void DrawOverlays(Mat frame)
    {
        var results = LastResults;

        foreach (var result in results)
        {
            var box = OverlayRenderer.ToOpenCvRect(result.FaceBox);
            OverlayRenderer.DrawFaceResult(
                frame,
                box,
                result.DisplayLabel,
                result.IsRecognized,
                result.IsHighConfidence,
                result.IsLive,
                result.IsSpoofDetected);
        }

        // Draw FPS and timing info
        OverlayRenderer.DrawFps(frame, 0); // Will be updated by CameraService

        if (LastTotalTime > TimeSpan.Zero)
        {
            OverlayRenderer.DrawStatus(frame,
                $"Detect: {LastDetectionTime.TotalMilliseconds:F0}ms | " +
                $"Embed: {LastEmbeddingTime.TotalMilliseconds:F0}ms | " +
                $"Search: {LastSearchTime.TotalMilliseconds:F0}ms | " +
                $"Total: {LastTotalTime.TotalMilliseconds:F0}ms");
        }
    }

    // ══════════════════════════════════════════════
    //  LIVENESS MANAGEMENT
    // ══════════════════════════════════════════════

    /// <summary>
    /// Reset the liveness tracking (e.g., when a new person appears).
    /// </summary>
    public void ResetLiveness() => _liveness.Reset();

    /// <summary>
    /// Get the current liveness status text for UI display.
    /// </summary>
    public string LivenessStatusText => _liveness.GetStatusText();

    /// <summary>
    /// Has a blink been detected?
    /// </summary>
    public bool IsLivenessConfirmed => _liveness.IsLive;

    // ══════════════════════════════════════════════
    //  CLEANUP
    // ══════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _detector.Dispose();
        _recognizer.Dispose();
        _liveness.Dispose();

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Result of a person registration attempt.
/// </summary>
public class RegistrationResult
{
    public bool Success { get; set; }
    public Person? Person { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// If the face matched an existing person, this is set.
    /// Allows the UI to offer "Add sample to existing person" instead.
    /// </summary>
    public Person? ExistingPerson { get; set; }
}
