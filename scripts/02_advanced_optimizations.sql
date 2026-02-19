-- ============================================================
-- Face Recognition PoC — Advanced SQL Server 2025 Optimizations
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
-- How it works:
--   - DiskANN builds a graph-based index on disk
--   - Queries search the graph instead of scanning all vectors
--   - Dramatically faster: O(log n) instead of O(n)
--
-- Performance improvement:
--   - 10,000 embeddings: exact ~10ms → DiskANN ~2ms
--   - 100,000 embeddings: exact ~100ms → DiskANN ~3ms
--   - 500,000 embeddings: exact ~500ms → DiskANN ~5ms
--
-- IMPORTANT LIMITATIONS (SQL Server 2025 Preview):
--   - The table becomes INSERT-ONLY while the vector index exists
--   - To UPDATE or DELETE rows, you must DROP the index first, then recreate
--   - This is a preview limitation that may be removed in future updates
--   - For the PoC with <1,000 faces, you likely don't need this
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
    -- Create the DiskANN vector index
    CREATE VECTOR INDEX IX_FaceEmbeddings_Vector
    ON FaceEmbeddings(Embedding)
    WITH (METRIC = 'cosine', TYPE = 'diskann');
    
    PRINT '✅ DiskANN vector index created on FaceEmbeddings.Embedding';
END
ELSE
BEGIN
    PRINT 'ℹ️ DiskANN vector index already exists.';
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

-- ──────────────────────────────────────────────
-- SP: Find closest face match (single best match)
-- ──────────────────────────────────────────────
-- This is the same query EF Core generates, but as a stored procedure.
-- Benefits:
--   - Pre-compiled execution plan (slightly faster)
--   - Can be called from any client (not just EF Core)
--   - Easier to tune and monitor
-- ──────────────────────────────────────────────
CREATE OR ALTER PROCEDURE sp_FindClosestFace
    @QueryEmbedding VECTOR(512),
    @DistanceThreshold FLOAT = 0.55
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP(1)
        p.Id AS PersonId,
        p.Name,
        p.ExternalId,
        p.Notes,
        e.Id AS EmbeddingId,
        VECTOR_DISTANCE('cosine', e.Embedding, @QueryEmbedding) AS Distance,
        CASE 
            WHEN VECTOR_DISTANCE('cosine', e.Embedding, @QueryEmbedding) <= @DistanceThreshold 
            THEN 1 ELSE 0 
        END AS IsMatch,
        CASE 
            WHEN VECTOR_DISTANCE('cosine', e.Embedding, @QueryEmbedding) <= 0.35 
            THEN 1 ELSE 0 
        END AS IsHighConfidence
    FROM FaceEmbeddings e
    INNER JOIN Persons p ON e.PersonId = p.Id
    WHERE p.IsActive = 1
    ORDER BY VECTOR_DISTANCE('cosine', e.Embedding, @QueryEmbedding) ASC;
END;
GO

PRINT '✅ sp_FindClosestFace created';
GO

-- ──────────────────────────────────────────────
-- SP: Find top N closest faces
-- ──────────────────────────────────────────────
CREATE OR ALTER PROCEDURE sp_FindTopFaces
    @QueryEmbedding VECTOR(512),
    @TopN INT = 5,
    @DistanceThreshold FLOAT = 0.55
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP(@TopN)
        p.Id AS PersonId,
        p.Name,
        p.ExternalId,
        e.Id AS EmbeddingId,
        e.CaptureAngle,
        VECTOR_DISTANCE('cosine', e.Embedding, @QueryEmbedding) AS Distance,
        CAST((1.0 - VECTOR_DISTANCE('cosine', e.Embedding, @QueryEmbedding)) * 100 AS DECIMAL(5,1)) AS [Similarity%],
        CASE 
            WHEN VECTOR_DISTANCE('cosine', e.Embedding, @QueryEmbedding) <= @DistanceThreshold 
            THEN 'MATCH' ELSE 'NO MATCH' 
        END AS Status
    FROM FaceEmbeddings e
    INNER JOIN Persons p ON e.PersonId = p.Id
    WHERE p.IsActive = 1
    ORDER BY VECTOR_DISTANCE('cosine', e.Embedding, @QueryEmbedding) ASC;
END;
GO

PRINT '✅ sp_FindTopFaces created';
GO

-- ──────────────────────────────────────────────
-- SP: Approximate search using DiskANN (VECTOR_SEARCH)
-- ──────────────────────────────────────────────
-- Uses the DiskANN index for approximate nearest neighbor search.
-- Much faster than exact search for large datasets.
-- Only works when the IX_FaceEmbeddings_Vector index exists.
-- ──────────────────────────────────────────────
CREATE OR ALTER PROCEDURE sp_FindClosestFace_Approximate
    @QueryEmbedding VECTOR(512),
    @TopN INT = 5,
    @DistanceThreshold FLOAT = 0.55
AS
BEGIN
    SET NOCOUNT ON;

    -- VECTOR_SEARCH uses the DiskANN index for approximate matching
    -- It's significantly faster but may miss some matches (99%+ recall)
    SELECT 
        p.Id AS PersonId,
        p.Name,
        p.ExternalId,
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
    INNER JOIN FaceEmbeddings e ON e.Id = vs.$rowid
    INNER JOIN Persons p ON e.PersonId = p.Id
    WHERE p.IsActive = 1
    ORDER BY vs.distance ASC;
END;
GO

PRINT '✅ sp_FindClosestFace_Approximate created';
GO

-- ============================================================
-- PART 3: Performance Benchmark Queries
-- ============================================================

-- Generate a test vector for benchmarking
DECLARE @testVector VECTOR(512);
SET @testVector = CAST(
    '[' + REPLICATE('0.05,', 511) + '0.05]'
    AS VECTOR(512)
);

-- Benchmark: Exact search timing
DECLARE @startTime DATETIME2 = SYSDATETIME();

SELECT TOP(1)
    p.Name,
    VECTOR_DISTANCE('cosine', e.Embedding, @testVector) AS Distance
FROM FaceEmbeddings e
INNER JOIN Persons p ON e.PersonId = p.Id
WHERE p.IsActive = 1
ORDER BY VECTOR_DISTANCE('cosine', e.Embedding, @testVector) ASC;

DECLARE @endTime DATETIME2 = SYSDATETIME();
SELECT 
    DATEDIFF(MICROSECOND, @startTime, @endTime) / 1000.0 AS [Exact Search (ms)],
    (SELECT COUNT(*) FROM FaceEmbeddings) AS [Total Embeddings];
GO

-- ============================================================
-- PART 4: Maintenance Views
-- ============================================================

-- View: Person summary with sample counts
CREATE OR ALTER VIEW vw_PersonSummary AS
SELECT 
    p.Id,
    p.Name,
    p.ExternalId,
    p.Notes,
    p.IsActive,
    COUNT(e.Id) AS FaceSampleCount,
    p.TotalRecognitions,
    p.CreatedAt,
    p.LastSeenAt,
    DATEDIFF(DAY, p.LastSeenAt, GETUTCDATE()) AS DaysSinceLastSeen
FROM Persons p
LEFT JOIN FaceEmbeddings e ON p.Id = e.PersonId
GROUP BY p.Id, p.Name, p.ExternalId, p.Notes, p.IsActive, 
         p.TotalRecognitions, p.CreatedAt, p.LastSeenAt;
GO

PRINT '✅ vw_PersonSummary view created';
GO

-- View: Recognition analytics
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

PRINT '✅ vw_RecognitionStats view created';
GO

-- Quick analytics query
SELECT * FROM vw_RecognitionStats ORDER BY RecognitionDate DESC;
GO

PRINT '✅ All advanced optimizations applied successfully!';
GO
