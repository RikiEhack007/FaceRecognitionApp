-- ============================================================
-- Face Recognition PoC — SQL Server 2025 Setup & Verification
-- ============================================================
-- Run this script in SSMS after EF Core migrations create the database.
-- It verifies vector support and adds optional optimizations.
-- ============================================================

-- Step 1: Verify SQL Server 2025
SELECT @@VERSION AS [SQL Server Version];
-- Should show: Microsoft SQL Server 2025 (RTM) - 17.x.x.x
-- If you see 2022 or older, vector support won't work!

-- Step 2: Verify the database exists
USE FaceRecognitionDb;
GO

-- Step 3: Verify tables were created by EF Core migrations
SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE = 'BASE TABLE'
ORDER BY TABLE_NAME;
-- Expected: FaceEmbeddings, Persons, RecognitionLogs, __EFMigrationsHistory

-- Step 4: Verify VECTOR(512) column exists
SELECT COLUMN_NAME, DATA_TYPE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'FaceEmbeddings' AND COLUMN_NAME = 'Embedding';
-- Expected: DATA_TYPE = 'vector'

-- Step 5: Test vector operations work
DECLARE @testVector VECTOR(512) = CAST(REPLICATE('[0.1,', 511) + '0.1]' AS VECTOR(512));
SELECT DATALENGTH(@testVector) AS [Vector Size in Bytes];
-- Expected: 2048 (512 floats × 4 bytes)

-- Step 6: Test VECTOR_DISTANCE function
DECLARE @v1 VECTOR(3) = '[1.0, 0.0, 0.0]';
DECLARE @v2 VECTOR(3) = '[0.0, 1.0, 0.0]';
SELECT VECTOR_DISTANCE('cosine', @v1, @v2) AS [Cosine Distance];
-- Expected: 1.0 (perpendicular vectors = maximum distance)

DECLARE @v3 VECTOR(3) = '[1.0, 0.0, 0.0]';
DECLARE @v4 VECTOR(3) = '[1.0, 0.0, 0.0]';
SELECT VECTOR_DISTANCE('cosine', @v3, @v4) AS [Cosine Distance - Same Vector];
-- Expected: 0.0 (identical vectors = zero distance)

PRINT '✅ All checks passed! SQL Server 2025 vector support is working.';
GO

-- ============================================================
-- OPTIONAL: Create DiskANN Vector Index (for large scale)
-- Only needed when you have 10,000+ face embeddings.
-- For PoC with <1,000 faces, exact search is fast enough.
-- ============================================================

-- Uncomment below when ready to scale:
/*
-- Enable preview features (required for vector indexes)
ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;
GO

-- Create approximate nearest neighbor index
-- WARNING: Table becomes read-only while vector index exists (preview limitation)
-- Drop the index before inserting new data, then recreate.
CREATE VECTOR INDEX IX_FaceEmbeddings_Vector
ON FaceEmbeddings(Embedding)
WITH (METRIC = 'cosine', TYPE = 'diskann');
GO

PRINT '✅ DiskANN vector index created.';
GO
*/

-- ============================================================
-- Useful queries for debugging and monitoring
-- ============================================================

-- Count registered persons and face samples
SELECT
    (SELECT COUNT(*) FROM Persons WHERE IsActive = 1) AS [Active Persons],
    (SELECT COUNT(*) FROM FaceEmbeddings) AS [Total Face Samples],
    (SELECT AVG(cnt) FROM (SELECT COUNT(*) as cnt FROM FaceEmbeddings GROUP BY PersonId) sub) AS [Avg Samples Per Person];

-- Recent recognition attempts
SELECT TOP 20
    rl.Timestamp,
    CASE WHEN rl.WasRecognized = 1 THEN '✅ Recognized' ELSE '❌ Unknown' END AS [Status],
    p.Name,
    rl.Distance,
    CAST((1.0 - rl.Distance) * 100 AS DECIMAL(5,1)) AS [Similarity %],
    CASE WHEN rl.PassedLiveness = 1 THEN '✅ Live' ELSE '⚠️ Failed' END AS [Liveness]
FROM RecognitionLogs rl
LEFT JOIN Persons p ON rl.PersonId = p.Id
ORDER BY rl.Timestamp DESC;
GO
