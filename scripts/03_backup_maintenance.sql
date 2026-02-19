-- ============================================================
-- Face Recognition PoC — Backup & Maintenance
-- ============================================================
-- Run periodically to maintain database health.
-- ============================================================

USE FaceRecognitionDb;
GO

-- ============================================================
-- PART 1: Backup
-- ============================================================
-- Change the path to your preferred backup location.
-- Default: C:\FaceRecBackups\

-- Create backup directory (run in CMD/PowerShell first):
-- mkdir C:\FaceRecBackups

DECLARE @backupPath NVARCHAR(500);
DECLARE @backupFile NVARCHAR(500);
DECLARE @timestamp NVARCHAR(20) = REPLACE(REPLACE(REPLACE(
    CONVERT(NVARCHAR, GETDATE(), 120), '-', ''), ':', ''), ' ', '_');

SET @backupPath = 'C:\FaceRecBackups\';
SET @backupFile = @backupPath + 'FaceRecognitionDb_' + @timestamp + '.bak';

-- Full backup
BACKUP DATABASE FaceRecognitionDb
TO DISK = @backupFile
WITH FORMAT, 
     MEDIANAME = 'FaceRecBackup',
     NAME = 'Full Backup of FaceRecognitionDb',
     COMPRESSION;

PRINT '✅ Backup created: ' + @backupFile;
GO

-- ============================================================
-- PART 2: Index Maintenance
-- ============================================================

-- Rebuild fragmented indexes
ALTER INDEX ALL ON Persons REBUILD;
ALTER INDEX ALL ON FaceEmbeddings REBUILD;
ALTER INDEX ALL ON RecognitionLogs REBUILD;

PRINT '✅ Indexes rebuilt';
GO

-- Update statistics for query optimizer
UPDATE STATISTICS Persons;
UPDATE STATISTICS FaceEmbeddings;
UPDATE STATISTICS RecognitionLogs;

PRINT '✅ Statistics updated';
GO

-- ============================================================
-- PART 3: Cleanup Old Logs
-- ============================================================
-- Keep recognition logs for 90 days, delete older ones.
-- Adjust retention period as needed.

DECLARE @cutoffDate DATETIME = DATEADD(DAY, -90, GETUTCDATE());
DECLARE @deletedCount INT;

DELETE FROM RecognitionLogs
WHERE Timestamp < @cutoffDate;

SET @deletedCount = @@ROWCOUNT;
PRINT '✅ Cleaned up ' + CAST(@deletedCount AS NVARCHAR) + ' old recognition logs (>90 days)';
GO

-- ============================================================
-- PART 4: Database Size Report
-- ============================================================

SELECT 
    DB_NAME() AS DatabaseName,
    CAST(SUM(size * 8.0 / 1024) AS DECIMAL(10,2)) AS [Total Size (MB)],
    CAST(SUM(CASE WHEN type = 0 THEN size * 8.0 / 1024 ELSE 0 END) AS DECIMAL(10,2)) AS [Data Size (MB)],
    CAST(SUM(CASE WHEN type = 1 THEN size * 8.0 / 1024 ELSE 0 END) AS DECIMAL(10,2)) AS [Log Size (MB)]
FROM sys.database_files;

-- Table-level breakdown
SELECT 
    t.name AS TableName,
    p.rows AS RowCount,
    CAST(SUM(a.total_pages) * 8.0 / 1024 AS DECIMAL(10,2)) AS [Size (MB)]
FROM sys.tables t
INNER JOIN sys.indexes i ON t.object_id = i.object_id
INNER JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
GROUP BY t.name, p.rows
ORDER BY SUM(a.total_pages) DESC;

-- Embedding storage estimate
SELECT 
    COUNT(*) AS TotalEmbeddings,
    CAST(COUNT(*) * 2048.0 / 1024 / 1024 AS DECIMAL(10,2)) AS [Estimated Vector Storage (MB)],
    CAST(50 * 1024 - (COUNT(*) * 2048.0 / 1024 / 1024) AS DECIMAL(10,2)) AS [Remaining Capacity (MB)],
    CAST(CAST(COUNT(*) * 2048.0 / 1024 / 1024 AS FLOAT) / (50 * 1024) * 100 AS DECIMAL(5,2)) AS [Usage %]
FROM FaceEmbeddings;

PRINT '✅ Database health report complete';
GO
