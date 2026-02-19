using FaceRecApp.Core.Services;
using FaceRecApp.Core.Entities;
using Xunit;

namespace FaceRecApp.Tests;

/// <summary>
/// Tests for the cosine similarity/distance computations.
/// These tests don't need a webcam, ONNX model, or SQL Server —
/// they test pure math on float arrays.
/// </summary>
public class SimilarityTests
{
    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        // Two identical vectors should have similarity = 1.0
        var a = CreateRandomVector(512, seed: 42);
        var b = (float[])a.Clone();

        float similarity = FaceRecognitionService.CosineSimilarity(a, b);

        Assert.InRange(similarity, 0.999f, 1.001f);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        // Two perpendicular vectors should have similarity ≈ 0.0
        var a = new float[512];
        var b = new float[512];
        a[0] = 1.0f;  // Points along dimension 0
        b[1] = 1.0f;  // Points along dimension 1

        float similarity = FaceRecognitionService.CosineSimilarity(a, b);

        Assert.InRange(similarity, -0.001f, 0.001f);
    }

    [Fact]
    public void CosineDistance_IdenticalVectors_ReturnsZero()
    {
        var a = CreateRandomVector(512, seed: 42);
        var b = (float[])a.Clone();

        float distance = FaceRecognitionService.CosineDistance(a, b);

        Assert.InRange(distance, -0.001f, 0.001f);
    }

    [Fact]
    public void CosineDistance_DifferentVectors_ReturnsPositive()
    {
        var a = CreateRandomVector(512, seed: 42);
        var b = CreateRandomVector(512, seed: 99); // Different seed = different vector

        float distance = FaceRecognitionService.CosineDistance(a, b);

        // Different random vectors should have positive distance
        Assert.True(distance > 0.0f, $"Expected positive distance, got {distance}");
        Assert.True(distance <= 2.0f, $"Cosine distance should be ≤ 2.0, got {distance}");
    }

    [Fact]
    public void IsSamePerson_BelowThreshold_ReturnsTrue()
    {
        // Create two very similar vectors (simulating same person)
        var a = CreateRandomVector(512, seed: 42);
        var b = (float[])a.Clone();

        // Add slight noise (simulating different photo of same person)
        var rng = new Random(99);
        for (int i = 0; i < b.Length; i++)
            b[i] += (float)(rng.NextDouble() - 0.5) * 0.05f;

        // Normalize b
        float norm = MathF.Sqrt(b.Sum(v => v * v));
        for (int i = 0; i < b.Length; i++)
            b[i] /= norm;

        bool result = FaceRecognitionService.IsSamePerson(a, b);

        // Small perturbation should still match
        Assert.True(result, "Slightly perturbed vectors should be recognized as same person");
    }

    [Fact]
    public void IsSamePerson_CompletelyDifferent_ReturnsFalse()
    {
        var a = CreateRandomVector(512, seed: 42);
        var b = CreateRandomVector(512, seed: 99);

        bool result = FaceRecognitionService.IsSamePerson(a, b);

        Assert.False(result, "Completely different vectors should NOT be same person");
    }

    [Fact]
    public void CosineSimilarity_DifferentDimensions_ThrowsException()
    {
        var a = new float[512];
        var b = new float[256]; // Wrong dimensions

        Assert.Throws<ArgumentException>(() =>
            FaceRecognitionService.CosineSimilarity(a, b));
    }

    [Fact]
    public void IsValidEmbedding_CorrectDimensions_ReturnsTrue()
    {
        var embedding = CreateRandomVector(512, seed: 42);
        // Normalize to unit length (like ArcFace output)
        float norm = MathF.Sqrt(embedding.Sum(v => v * v));
        for (int i = 0; i < embedding.Length; i++)
            embedding[i] /= norm;

        Assert.True(FaceRecognitionService.IsValidEmbedding(embedding));
    }

    [Fact]
    public void IsValidEmbedding_WrongDimensions_ReturnsFalse()
    {
        var embedding = new float[256]; // Should be 512
        Assert.False(FaceRecognitionService.IsValidEmbedding(embedding));
    }

    [Fact]
    public void IsValidEmbedding_AllZeros_ReturnsFalse()
    {
        var embedding = new float[512]; // All zeros
        Assert.False(FaceRecognitionService.IsValidEmbedding(embedding));
    }

    [Fact]
    public void IsValidEmbedding_Null_ReturnsFalse()
    {
        Assert.False(FaceRecognitionService.IsValidEmbedding(null!));
    }

    [Fact]
    public void DistanceThreshold_MatchesSimilarityExpectation()
    {
        // Verify our threshold settings make sense:
        // Distance 0.55 = Similarity 0.45 (45%)
        float expectedSimilarity = 1f - RecognitionSettings.DistanceThreshold;
        Assert.InRange(expectedSimilarity, 0.40f, 0.50f);

        // High confidence distance 0.35 = Similarity 0.65 (65%)
        float highConfSimilarity = 1f - RecognitionSettings.HighConfidenceDistance;
        Assert.InRange(highConfSimilarity, 0.60f, 0.70f);
    }

    // ──────────────────────────────────────────────
    // Helper: Create normalized random vector
    // ──────────────────────────────────────────────

    private static float[] CreateRandomVector(int dimensions, int seed)
    {
        var rng = new Random(seed);
        var vector = new float[dimensions];

        for (int i = 0; i < dimensions; i++)
            vector[i] = (float)(rng.NextDouble() * 2 - 1); // Range [-1, 1]

        // L2 normalize (like ArcFace output)
        float norm = MathF.Sqrt(vector.Sum(v => v * v));
        for (int i = 0; i < dimensions; i++)
            vector[i] /= norm;

        return vector;
    }
}
