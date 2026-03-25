using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FaceRecApp.Core.Migrations
{
    /// <inheritdoc />
    public partial class SplitBiometricTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Patients",
                columns: table => new
                {
                    IDCard = table.Column<string>(type: "varchar(10)", nullable: false),
                    Site = table.Column<string>(type: "varchar(10)", nullable: false),
                    AdmissionDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FullName = table.Column<string>(type: "varchar(100)", nullable: true),
                    BurmeseName = table.Column<string>(type: "nvarchar(100)", nullable: true),
                    KarenName = table.Column<string>(type: "nvarchar(100)", nullable: true),
                    MotherPID = table.Column<string>(type: "varchar(10)", nullable: true),
                    MotherName = table.Column<string>(type: "varchar(255)", nullable: true),
                    FatherName = table.Column<string>(type: "varchar(255)", nullable: true),
                    SpouseName = table.Column<string>(type: "varchar(100)", nullable: true),
                    Sex = table.Column<byte>(type: "tinyint", nullable: true),
                    Age = table.Column<byte>(type: "tinyint", nullable: true),
                    Month = table.Column<byte>(type: "tinyint", nullable: true),
                    Day = table.Column<byte>(type: "tinyint", nullable: true),
                    DOB_year = table.Column<short>(type: "smallint", nullable: true),
                    DOB_month = table.Column<short>(type: "smallint", nullable: true),
                    DOB_day = table.Column<short>(type: "smallint", nullable: true),
                    AddressCode = table.Column<string>(type: "varchar(50)", nullable: true),
                    AddressOther = table.Column<string>(type: "varchar(max)", nullable: true),
                    PhoneNumber = table.Column<string>(type: "varchar(50)", nullable: true),
                    Note = table.Column<string>(type: "varchar(max)", nullable: true),
                    LastModified = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSync = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedBy = table.Column<string>(type: "varchar(50)", nullable: true),
                    CreatedOn = table.Column<string>(type: "varchar(50)", nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(50)", nullable: true),
                    ModifiedOn = table.Column<string>(type: "varchar(50)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Patients", x => x.IDCard);
                });

            migrationBuilder.CreateTable(
                name: "FaceEmbeddings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CapturedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PID = table.Column<string>(type: "varchar(10)", nullable: false),
                    Embedding = table.Column<string>(type: "vector(512)", nullable: false),
                    FaceThumbnail = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CaptureAngle = table.Column<string>(type: "varchar(20)", nullable: true),
                    QualityScore = table.Column<float>(type: "real", nullable: true),
                    Consent = table.Column<bool>(type: "bit", nullable: false),
                    Remark = table.Column<string>(type: "varchar(100)", nullable: true),
                    CreatedBy = table.Column<string>(type: "varchar(50)", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(50)", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceEmbeddings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FaceEmbeddings_Patients_PID",
                        column: x => x.PID,
                        principalTable: "Patients",
                        principalColumn: "IDCard",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FingerprintTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PID = table.Column<string>(type: "varchar(10)", nullable: false),
                    FingerType = table.Column<string>(type: "varchar(20)", nullable: false),
                    Template = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CaptureDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Consent = table.Column<bool>(type: "bit", nullable: false),
                    ConsentRefusalReason = table.Column<string>(type: "varchar(500)", nullable: true),
                    Remark = table.Column<string>(type: "varchar(100)", nullable: true),
                    CreatedBy = table.Column<string>(type: "varchar(50)", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(50)", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FingerprintTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FingerprintTemplates_Patients_PID",
                        column: x => x.PID,
                        principalTable: "Patients",
                        principalColumn: "IDCard",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecognitionLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PID = table.Column<string>(type: "varchar(10)", nullable: true),
                    Distance = table.Column<float>(type: "real", nullable: false),
                    WasRecognized = table.Column<bool>(type: "bit", nullable: false),
                    PassedLiveness = table.Column<bool>(type: "bit", nullable: false),
                    StationId = table.Column<string>(type: "varchar(50)", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecognitionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecognitionLogs_Patients_PID",
                        column: x => x.PID,
                        principalTable: "Patients",
                        principalColumn: "IDCard",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Visits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PID = table.Column<string>(type: "varchar(10)", nullable: false),
                    Date = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CC = table.Column<string>(type: "varchar(500)", nullable: true),
                    ServiceType = table.Column<string>(type: "varchar(50)", nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(50)", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "varchar(50)", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Visits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Visits_Patients_PID",
                        column: x => x.PID,
                        principalTable: "Patients",
                        principalColumn: "IDCard",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FaceEmbeddings_PID",
                table: "FaceEmbeddings",
                column: "PID");

            migrationBuilder.CreateIndex(
                name: "IX_FingerprintTemplates_FingerType",
                table: "FingerprintTemplates",
                column: "FingerType");

            migrationBuilder.CreateIndex(
                name: "IX_FingerprintTemplates_PID",
                table: "FingerprintTemplates",
                column: "PID");

            migrationBuilder.CreateIndex(
                name: "IX_Patients_FullName",
                table: "Patients",
                column: "FullName");

            migrationBuilder.CreateIndex(
                name: "IX_Patients_Site",
                table: "Patients",
                column: "Site");

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionLogs_PID",
                table: "RecognitionLogs",
                column: "PID");

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionLogs_Timestamp",
                table: "RecognitionLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionLogs_WasRecognized",
                table: "RecognitionLogs",
                column: "WasRecognized");

            migrationBuilder.CreateIndex(
                name: "IX_Visits_Date",
                table: "Visits",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Visits_PID",
                table: "Visits",
                column: "PID");

            migrationBuilder.CreateIndex(
                name: "IX_Visits_ServiceType",
                table: "Visits",
                column: "ServiceType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FaceEmbeddings");

            migrationBuilder.DropTable(
                name: "FingerprintTemplates");

            migrationBuilder.DropTable(
                name: "RecognitionLogs");

            migrationBuilder.DropTable(
                name: "Visits");

            migrationBuilder.DropTable(
                name: "Patients");
        }
    }
}
