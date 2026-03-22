IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
CREATE TABLE [Patients] (
    [IDCard] varchar(10) NOT NULL,
    [Site] varchar(10) NOT NULL,
    [AdmissionDate] datetime2 NULL,
    [FullName] varchar(100) NULL,
    [BurmeseName] nvarchar(100) NULL,
    [KarenName] nvarchar(100) NULL,
    [MotherPID] varchar(10) NULL,
    [MotherName] varchar(255) NULL,
    [FatherName] varchar(255) NULL,
    [SpouseName] varchar(100) NULL,
    [Sex] tinyint NULL,
    [Age] tinyint NULL,
    [Month] tinyint NULL,
    [Day] tinyint NULL,
    [DOB_year] smallint NULL,
    [DOB_month] smallint NULL,
    [DOB_day] smallint NULL,
    [AddressCode] varchar(50) NULL,
    [AddressOther] varchar(max) NULL,
    [PhoneNumber] varchar(50) NULL,
    [Note] varchar(max) NULL,
    [LastModified] datetime2 NULL,
    [LastSync] datetime2 NULL,
    [CreatedBy] varchar(50) NULL,
    [CreatedOn] varchar(50) NULL,
    [ModifiedBy] varchar(50) NULL,
    [ModifiedOn] varchar(50) NULL,
    CONSTRAINT [PK_Patients] PRIMARY KEY ([IDCard])
);

CREATE TABLE [Biometrics] (
    [Id] int NOT NULL IDENTITY,
    [Date] datetime2 NOT NULL,
    [PID] varchar(10) NOT NULL,
    [BiometricType] varchar(20) NOT NULL,
    [Embedding] vector(512) NULL,
    [FaceThumbnail] varbinary(max) NULL,
    [CaptureAngle] varchar(20) NULL,
    [QualityScore] real NULL,
    [Template] varbinary(max) NULL,
    [Remark] varchar(100) NULL,
    [Consent] bit NOT NULL,
    [ConsentRefusalReason] varchar(500) NULL,
    [CreatedBy] varchar(50) NULL,
    [CreatedDate] datetime2 NULL,
    [ModifiedBy] varchar(50) NULL,
    [ModifiedDate] datetime2 NULL,
    CONSTRAINT [PK_Biometrics] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Biometrics_Patients_PID] FOREIGN KEY ([PID]) REFERENCES [Patients] ([IDCard]) ON DELETE CASCADE
);

CREATE TABLE [RecognitionLogs] (
    [Id] int NOT NULL IDENTITY,
    [PID] varchar(10) NULL,
    [Distance] real NOT NULL,
    [WasRecognized] bit NOT NULL,
    [PassedLiveness] bit NOT NULL,
    [StationId] varchar(50) NULL,
    [Timestamp] datetime2 NOT NULL,
    CONSTRAINT [PK_RecognitionLogs] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_RecognitionLogs_Patients_PID] FOREIGN KEY ([PID]) REFERENCES [Patients] ([IDCard]) ON DELETE SET NULL
);

CREATE TABLE [Visits] (
    [Id] int NOT NULL IDENTITY,
    [PID] varchar(10) NOT NULL,
    [Date] datetime2 NOT NULL,
    [CC] varchar(500) NULL,
    [ServiceType] varchar(50) NOT NULL,
    [CreatedBy] varchar(50) NULL,
    [CreatedDate] datetime2 NULL,
    [ModifiedBy] varchar(50) NULL,
    [ModifiedDate] datetime2 NULL,
    CONSTRAINT [PK_Visits] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Visits_Patients_PID] FOREIGN KEY ([PID]) REFERENCES [Patients] ([IDCard]) ON DELETE CASCADE
);

CREATE INDEX [IX_Biometrics_BiometricType] ON [Biometrics] ([BiometricType]);

CREATE INDEX [IX_Biometrics_PID] ON [Biometrics] ([PID]);

CREATE INDEX [IX_Patients_FullName] ON [Patients] ([FullName]);

CREATE INDEX [IX_Patients_Site] ON [Patients] ([Site]);

CREATE INDEX [IX_RecognitionLogs_PID] ON [RecognitionLogs] ([PID]);

CREATE INDEX [IX_RecognitionLogs_Timestamp] ON [RecognitionLogs] ([Timestamp]);

CREATE INDEX [IX_RecognitionLogs_WasRecognized] ON [RecognitionLogs] ([WasRecognized]);

CREATE INDEX [IX_Visits_Date] ON [Visits] ([Date]);

CREATE INDEX [IX_Visits_PID] ON [Visits] ([PID]);

CREATE INDEX [IX_Visits_ServiceType] ON [Visits] ([ServiceType]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260313111125_FreshBiometricSchema', N'9.0.13');

COMMIT;
GO

