using FaceRecApp.Core.Entities;
using Xunit;

namespace FaceRecApp.Tests;

/// <summary>
/// Basic entity tests — these run without SQL Server (pure C# tests).
/// Database integration tests will be added in later sections.
/// </summary>
public class EntityTests
{
    [Fact]
    public void Person_DefaultValues_AreCorrect()
    {
        var person = new Person { Name = "John Doe" };

        Assert.Equal("John Doe", person.Name);
        Assert.True(person.IsActive);
        Assert.Equal(0, person.TotalRecognitions);
        Assert.NotEmpty(person.FaceEmbeddings.GetType().Name); // Collection initialized
    }

    [Fact]
    public void FaceEmbedding_EmptyByDefault()
    {
        var embedding = new FaceEmbedding();

        Assert.Empty(embedding.Embedding);
        Assert.Null(embedding.FaceThumbnail);
        Assert.Null(embedding.CaptureAngle);
    }

    [Fact]
    public void FaceEmbedding_Stores512Dimensions()
    {
        // ArcFace outputs 512 dimensions
        var vector = new float[RecognitionSettings.EmbeddingDimensions];
        for (int i = 0; i < vector.Length; i++)
            vector[i] = (float)(i * 0.01);

        var embedding = new FaceEmbedding
        {
            PersonId = 1,
            Embedding = vector
        };

        Assert.Equal(512, embedding.Embedding.Length);
        Assert.InRange(embedding.Embedding[100], 0.99f, 1.01f); // ~1.0
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
