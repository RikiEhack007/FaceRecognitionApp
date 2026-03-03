using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FaceRecApp.Core.Migrations
{
    /// <inheritdoc />
    public partial class CleanBiometricSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Add new columns to FaceEmbeddings ──
            migrationBuilder.AddColumn<bool>(
                name: "Consent",
                table: "FaceEmbeddings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "FaceEmbeddings",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            // ── 2. Create FingerprintTemplates table ──
            migrationBuilder.CreateTable(
                name: "FingerprintTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PersonId = table.Column<int>(type: "int", nullable: false),
                    FingerType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Template = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CaptureDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Consent = table.Column<bool>(type: "bit", nullable: false),
                    Remark = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ConsentRefusalReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FingerprintTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FingerprintTemplates_Patients_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FingerprintTemplates_FingerType",
                table: "FingerprintTemplates",
                column: "FingerType");

            migrationBuilder.CreateIndex(
                name: "IX_FingerprintTemplates_PersonId",
                table: "FingerprintTemplates",
                column: "PersonId");

            // ── 3. Migrate data: fingerprints from Biometrics → FingerprintTemplates ──
            migrationBuilder.Sql(@"
                INSERT INTO FingerprintTemplates
                    (PersonId, FingerType, Template, CaptureDate, Consent,
                     Remark, ConsentRefusalReason, CreatedBy, ModifiedBy, ModifiedDate)
                SELECT
                    PersonId, BiometricType, Template, CaptureDate, Consent,
                    Remark, ConsentRefusalReason, CreatedBy, ModifiedBy, ModifiedDate
                FROM Biometrics
                WHERE BiometricType LIKE 'Finger%'
            ");

            // ── 4. Migrate data: face consent from Biometrics → FaceEmbeddings ──
            migrationBuilder.Sql(@"
                UPDATE fe
                SET fe.Consent = b.Consent,
                    fe.CreatedBy = b.CreatedBy
                FROM FaceEmbeddings fe
                INNER JOIN Biometrics b ON b.FaceEmbeddingId = fe.Id
                WHERE b.BiometricType = 'Face'
                  AND b.FaceEmbeddingId IS NOT NULL
            ");

            // ── 5. Preserve face remarks in Patient.Notes ──
            migrationBuilder.Sql(@"
                UPDATE p
                SET p.Notes = CASE
                    WHEN p.Notes IS NULL OR p.Notes = ''
                        THEN '[Face] ' + b.Remark
                    ELSE p.Notes + CHAR(10) + '[Face] ' + b.Remark
                    END
                FROM Patients p
                INNER JOIN Biometrics b ON b.PersonId = p.Id
                WHERE b.BiometricType = 'Face'
                  AND b.Remark IS NOT NULL
                  AND b.FaceEmbeddingId IS NULL
            ");

            // ── 6. Drop old Biometrics table (all data migrated) ──
            migrationBuilder.DropTable(
                name: "Biometrics");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FingerprintTemplates");

            migrationBuilder.DropColumn(
                name: "Consent",
                table: "FaceEmbeddings");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "FaceEmbeddings");

            migrationBuilder.CreateTable(
                name: "Biometrics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FaceEmbeddingId = table.Column<int>(type: "int", nullable: true),
                    PersonId = table.Column<int>(type: "int", nullable: false),
                    BiometricType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CaptureDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Consent = table.Column<bool>(type: "bit", nullable: false),
                    ConsentRefusalReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Remark = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Template = table.Column<byte[]>(type: "varbinary(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Biometrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Biometrics_FaceEmbeddings_FaceEmbeddingId",
                        column: x => x.FaceEmbeddingId,
                        principalTable: "FaceEmbeddings",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Biometrics_Patients_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Biometrics_BiometricType",
                table: "Biometrics",
                column: "BiometricType");

            migrationBuilder.CreateIndex(
                name: "IX_Biometrics_FaceEmbeddingId",
                table: "Biometrics",
                column: "FaceEmbeddingId");

            migrationBuilder.CreateIndex(
                name: "IX_Biometrics_PersonId",
                table: "Biometrics",
                column: "PersonId");
        }
    }
}
