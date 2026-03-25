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
        Assert.NotEmpty(patient.FaceEmbeddings.GetType().Name);
        Assert.NotEmpty(patient.FingerprintTemplates.GetType().Name);
    }

    [Fact]
    public void FaceEmbedding_EmptyByDefault()
    {
        var face = new FaceEmbedding();

        Assert.NotNull(face.Embedding);
        Assert.Empty(face.Embedding);
        Assert.Null(face.FaceThumbnail);
        Assert.Null(face.CaptureAngle);
        Assert.True(face.Consent);
    }

    [Fact]
    public void FaceEmbedding_Stores512Dimensions()
    {
        var vector = new float[RecognitionSettings.EmbeddingDimensions];
        for (int i = 0; i < vector.Length; i++)
            vector[i] = (float)(i * 0.01);

        var face = new FaceEmbedding
        {
            PID = "R00001",
            Embedding = vector
        };

        Assert.Equal(512, face.Embedding.Length);
        Assert.InRange(face.Embedding[100], 0.99f, 1.01f);
    }

    [Fact]
    public void FingerprintTemplate_DefaultValues()
    {
        var fp = new FingerprintTemplate
        {
            PID = "R00001",
            FingerType = BiometricRemarks.Types.FingerR2,
        };

        Assert.Null(fp.Template);
        Assert.True(fp.Consent);
        Assert.Equal("FingerR2", fp.FingerType);
    }

    [Fact]
    public void RecognitionLog_SimilarityCalculation()
    {
        var log = new RecognitionLog { Distance = 0.3f };
        Assert.InRange(log.Similarity, 0.69f, 0.71f);

        var perfect = new RecognitionLog { Distance = 0.0f };
        Assert.Equal(1.0f, perfect.Similarity);

        var noMatch = new RecognitionLog { Distance = 1.0f };
        Assert.Equal(0.0f, noMatch.Similarity);
    }

    [Fact]
    public void RecognitionSettings_ThresholdsAreSane()
    {
        Assert.InRange(RecognitionSettings.DistanceThreshold, 0f, 1f);
        Assert.True(RecognitionSettings.HighConfidenceDistance < RecognitionSettings.DistanceThreshold);
        Assert.Equal(512, RecognitionSettings.EmbeddingDimensions);
    }
}
