using FaceRecApp.Core.Entities;
using Xunit;

namespace FaceRecApp.Tests;

/// <summary>
/// Basic entity tests — these run without SQL Server (pure C# tests).
/// </summary>
public class EntityTests
{
    [Fact]
    public void Patient_DefaultValues_AreCorrect()
    {
        var patient = new Patient { FullName = "John Doe", IDCard = "R00001" };

        Assert.Equal("John Doe", patient.FullName);
        Assert.NotEmpty(patient.Biometrics.GetType().Name); // Collection initialized
    }

    [Fact]
    public void Biometric_Face_EmptyByDefault()
    {
        var biometric = new Biometric
        {
            BiometricType = BiometricRemarks.Types.Face,
        };

        Assert.Null(biometric.Embedding);
        Assert.Null(biometric.FaceThumbnail);
        Assert.Null(biometric.CaptureAngle);
        Assert.True(biometric.IsFace);
        Assert.False(biometric.IsFingerprint);
    }

    [Fact]
    public void Biometric_Face_Stores512Dimensions()
    {
        // ArcFace outputs 512 dimensions
        var vector = new float[RecognitionSettings.EmbeddingDimensions];
        for (int i = 0; i < vector.Length; i++)
            vector[i] = (float)(i * 0.01);

        var biometric = new Biometric
        {
            PID = "R00001",
            BiometricType = BiometricRemarks.Types.Face,
            Embedding = vector
        };

        Assert.Equal(512, biometric.Embedding!.Length);
        Assert.InRange(biometric.Embedding[100], 0.99f, 1.01f); // ~1.0
    }

    [Fact]
    public void RecognitionLog_SimilarityCalculation()
    {
        // Distance 0.3 → Similarity 0.7 (70%)
        var log = new RecognitionLog { Distance = 0.3f };
        Assert.InRange(log.Similarity, 0.69f, 0.71f);

        // Distance 0.0 → Similarity 1.0 (100% - perfect match)
        var perfect = new RecognitionLog { Distance = 0.0f };
        Assert.Equal(1.0f, perfect.Similarity);

        // Distance 1.0 → Similarity 0.0 (0% - no match)
        var noMatch = new RecognitionLog { Distance = 1.0f };
        Assert.Equal(0.0f, noMatch.Similarity);
    }

    [Fact]
    public void RecognitionSettings_ThresholdsAreSane()
    {
        // Distance threshold must be between 0 and 1
        Assert.InRange(RecognitionSettings.DistanceThreshold, 0f, 1f);

        // High confidence must be stricter (lower) than general threshold
        Assert.True(RecognitionSettings.HighConfidenceDistance < RecognitionSettings.DistanceThreshold);

        // Embedding dimensions must be 512 (ArcFace standard)
        Assert.Equal(512, RecognitionSettings.EmbeddingDimensions);
    }
}
