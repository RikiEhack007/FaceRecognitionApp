-- ============================================================
-- Face Recognition App — Advanced SQL Server 2025 Optimizations
-- ============================================================
-- Run AFTER you have data in the database and want to optimize.
-- ============================================================

USE FaceRecognitionDb;
GO

-- ============================================================
-- PART 1: DiskANN Vector Index
-- ============================================================
-- Creates an approximate nearest neighbor index on the VECTOR(512) column.
--
-- When to use:
--   - When you have 5,000+ face embeddings
--   - When exact search becomes too slow (>50ms)
--   - When approximate results are acceptable (99%+ recall)
--
-- Performance improvement:
--   - 10,000 embeddings: exact ~10ms -> DiskANN ~2ms
--   - 100,000 embeddings: exact ~100ms -> DiskANN ~3ms
--   - 500,000 embeddings: exact ~500ms -> DiskANN ~5ms
--
-- IMPORTANT LIMITATIONS (SQL Server 2025 Preview):
--   - The table becomes INSERT-ONLY while the vector index exists
--   - To UPDATE or DELETE rows, you must DROP the index first, then recreate
-- ============================================================

-- Step 1: Enable preview features (required for vector indexes)
ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;
GO

-- Step 2: Check if index already exists
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_FaceEmbeddings_Vector'
    AND object_id = OBJECT_ID('FaceEmbeddings')
)
BEGIN
    CREATE VECTOR INDEX IX_FaceEmbeddings_Vector
    ON FaceEmbeddings(Embedding)
    WITH (METRIC = 'cosine', TYPE = 'diskann');

    PRINT 'DiskANN vector index created on FaceEmbeddings.Embedding';
END
ELSE
BEGIN
    PRINT 'DiskANN vector index already exists.';
END
GO

-- Step 3: Verify the index exists
SELECT
    i.name AS IndexName,
    i.type_desc AS IndexType,
    OBJECT_NAME(i.object_id) AS TableName
FROM sys.indexes i
WHERE i.name = 'IX_FaceEmbeddings_Vector';
GO

-- ============================================================
-- PART 2: Stored Procedures for Optimized Matching
-- ============================================================

-- SP: Find closest face match (single best match)
CREATE OR ALTER PROCEDURE sp_FindClosestFace
    @QueryEmbedding VECTOR(512),
    @DistanceThreshold FLOAT = 0.55
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP(1)
        p.IDCard AS PID,
        p.FullName,
        p.Site,
        fe.Id AS FaceEmbeddingId,
        VECTOR_DISTANCE('cosine', fe.Embedding, @QueryEmbedding) AS Distance,
        CASE
            WHEN VECTOR_DISTANCE('cosine', fe.Embedding, @QueryEmbedding) <= @DistanceThreshold
            THEN 1 ELSE 0
        END AS IsMatch,
        CASE
            WHEN VECTOR_DISTANCE('cosine', fe.Embedding, @QueryEmbedding) <= 0.35
            THEN 1 ELSE 0
        END AS IsHighConfidence
    FROM FaceEmbeddings fe
    INNER JOIN Patients p ON fe.PID = p.IDCard
    WHERE fe.Embedding IS NOT NULL
    ORDER BY VECTOR_DISTANCE('cosine', fe.Embedding, @QueryEmbedding) ASC;
END;
GO

PRINT 'sp_FindClosestFace created';
GO

-- SP: Find top N closest faces
CREATE OR ALTER PROCEDURE sp_FindTopFaces
    @QueryEmbedding VECTOR(512),
    @TopN INT = 5,
    @DistanceThreshold FLOAT = 0.55
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP(@TopN)
        p.IDCard AS PID,
        p.FullName,
        p.Site,
        fe.Id AS FaceEmbeddingId,
        fe.CaptureAngle,
        VECTOR_DISTANCE('cosine', fe.Embedding, @QueryEmbedding) AS Distance,
        CAST((1.0 - VECTOR_DISTANCE('cosine', fe.Embedding, @QueryEmbedding)) * 100 AS DECIMAL(5,1)) AS [Similarity%],
        CASE
            WHEN VECTOR_DISTANCE('cosine', fe.Embedding, @QueryEmbedding) <= @DistanceThreshold
            THEN 'MATCH' ELSE 'NO MATCH'
        END AS Status
    FROM FaceEmbeddings fe
    INNER JOIN Patients p ON fe.PID = p.IDCard
    WHERE fe.Embedding IS NOT NULL
    ORDER BY VECTOR_DISTANCE('cosine', fe.Embedding, @QueryEmbedding) ASC;
END;
GO

PRINT 'sp_FindTopFaces created';
GO

-- SP: Approximate search using DiskANN (VECTOR_SEARCH)
CREATE OR ALTER PROCEDURE sp_FindClosestFace_Approximate
    @QueryEmbedding VECTOR(512),
    @TopN INT = 5,
    @DistanceThreshold FLOAT = 0.55
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        p.IDCard AS PID,
        p.FullName,
        p.Site,
        vs.distance AS Distance,
        CAST((1.0 - vs.distance) * 100 AS DECIMAL(5,1)) AS [Similarity%],
        CASE
            WHEN vs.distance <= @DistanceThreshold
            THEN 'MATCH' ELSE 'NO MATCH'
        END AS Status
    FROM VECTOR_SEARCH(
        FaceEmbeddings, Embedding, @QueryEmbedding,
        'metric=cosine', @TopN
    ) vs
    INNER JOIN FaceEmbeddings fe ON fe.Id = vs.$rowid
    INNER JOIN Patients p ON fe.PID = p.IDCard
    ORDER BY vs.distance ASC;
END;
GO

PRINT 'sp_FindClosestFace_Approximate created';
GO

-- ============================================================
-- PART 3: Performance Benchmark Queries
-- ============================================================

DECLARE @testVector VECTOR(512);
SET @testVector = CAST(
    '[' + REPLICATE('0.05,', 511) + '0.05]'
    AS VECTOR(512)
);

DECLARE @startTime DATETIME2 = SYSDATETIME();

SELECT TOP(1)
    p.FullName,
    VECTOR_DISTANCE('cosine', fe.Embedding, @testVector) AS Distance
FROM FaceEmbeddings fe
INNER JOIN Patients p ON fe.PID = p.IDCard
WHERE fe.Embedding IS NOT NULL
ORDER BY VECTOR_DISTANCE('cosine', fe.Embedding, @testVector) ASC;

DECLARE @endTime DATETIME2 = SYSDATETIME();
SELECT
    DATEDIFF(MICROSECOND, @startTime, @endTime) / 1000.0 AS [Exact Search (ms)],
    (SELECT COUNT(*) FROM FaceEmbeddings) AS [Total Face Embeddings];
GO

-- ============================================================
-- PART 4: Maintenance Views
-- ============================================================

CREATE OR ALTER VIEW vw_PatientSummary AS
SELECT
    p.IDCard AS PID,
    p.FullName,
    p.Site,
    p.Sex,
    p.Note,
    (SELECT COUNT(*) FROM FaceEmbeddings fe WHERE fe.PID = p.IDCard) AS FaceSampleCount,
    (SELECT COUNT(*) FROM FingerprintTemplates ft WHERE ft.PID = p.IDCard) AS FingerprintCount,
    p.CreatedOn
FROM Patients p;
GO

PRINT 'vw_PatientSummary view created';
GO

CREATE OR ALTER VIEW vw_RecognitionStats AS
SELECT
    CAST(Timestamp AS DATE) AS RecognitionDate,
    COUNT(*) AS TotalAttempts,
    SUM(CASE WHEN WasRecognized = 1 THEN 1 ELSE 0 END) AS SuccessfulMatches,
    SUM(CASE WHEN WasRecognized = 0 THEN 1 ELSE 0 END) AS UnknownFaces,
    SUM(CASE WHEN PassedLiveness = 0 THEN 1 ELSE 0 END) AS LivenessFailures,
    AVG(Distance) AS AvgDistance,
    MIN(Distance) AS BestDistance,
    CAST(
        SUM(CASE WHEN WasRecognized = 1 THEN 1.0 ELSE 0 END) /
        NULLIF(COUNT(*), 0) * 100
    AS DECIMAL(5,1)) AS RecognitionRate
FROM RecognitionLogs
GROUP BY CAST(Timestamp AS DATE);
GO

PRINT 'vw_RecognitionStats view created';
GO

SELECT * FROM vw_RecognitionStats ORDER BY RecognitionDate DESC;
GO

PRINT 'All advanced optimizations applied successfully!';
GO
