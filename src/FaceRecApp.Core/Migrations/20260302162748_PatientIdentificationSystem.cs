using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FaceRecApp.Core.Migrations
{
    /// <inheritdoc />
    public partial class PatientIdentificationSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FaceEmbeddings_Persons_PersonId",
                table: "FaceEmbeddings");

            migrationBuilder.DropForeignKey(
                name: "FK_RecognitionLogs_Persons_PersonId",
                table: "RecognitionLogs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Persons",
                table: "Persons");

            migrationBuilder.RenameTable(
                name: "Persons",
                newName: "Patients");

            migrationBuilder.RenameColumn(
                name: "Name",
                table: "Patients",
                newName: "FullName");

            migrationBuilder.RenameIndex(
                name: "IX_Persons_Name",
                table: "Patients",
                newName: "IX_Patients_FullName");

            migrationBuilder.RenameIndex(
                name: "IX_Persons_IsActive",
                table: "Patients",
                newName: "IX_Patients_IsActive");

            migrationBuilder.RenameIndex(
                name: "IX_Persons_ExternalId",
                table: "Patients",
                newName: "IX_Patients_ExternalId");

            migrationBuilder.AddColumn<string>(
                name: "AddressCode",
                table: "Patients",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressOther",
                table: "Patients",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AdmissionDate",
                table: "Patients",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "AgeAtEnrolment",
                table: "Patients",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConsentDate",
                table: "Patients",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConsentGiven",
                table: "Patients",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "Patients",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "DOBDay",
                table: "Patients",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "DOBMonth",
                table: "Patients",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "DOBYear",
                table: "Patients",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "DayAtEnrolment",
                table: "Patients",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FatherName",
                table: "Patients",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IDCard",
                table: "Patients",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSync",
                table: "Patients",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "Patients",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedDate",
                table: "Patients",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "MonthAtEnrolment",
                table: "Patients",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MotherName",
                table: "Patients",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MotherPID",
                table: "Patients",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Sex",
                table: "Patients",
                type: "tinyint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Site",
                table: "Patients",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpouseName",
                table: "Patients",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Patients",
                table: "Patients",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "Biometrics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PersonId = table.Column<int>(type: "int", nullable: false),
                    FaceEmbeddingId = table.Column<int>(type: "int", nullable: true),
                    CaptureDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BiometricType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Template = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Remark = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Consent = table.Column<bool>(type: "bit", nullable: false),
                    ConsentRefusalReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "Visits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PersonId = table.Column<int>(type: "int", nullable: false),
                    VisitDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChiefComplaint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ServiceType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Visits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Visits_Patients_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Backfill existing rows with unique IDCard values before adding unique index
            migrationBuilder.Sql(
                "UPDATE Patients SET IDCard = 'X' + RIGHT('00000' + CAST(Id AS VARCHAR(5)), 5) WHERE IDCard = '' OR IDCard IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Patients_IDCard",
                table: "Patients",
                column: "IDCard",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Patients_Site",
                table: "Patients",
                column: "Site");

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

            migrationBuilder.CreateIndex(
                name: "IX_Visits_PersonId",
                table: "Visits",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Visits_ServiceType",
                table: "Visits",
                column: "ServiceType");

            migrationBuilder.CreateIndex(
                name: "IX_Visits_VisitDate",
                table: "Visits",
                column: "VisitDate");

            migrationBuilder.AddForeignKey(
                name: "FK_FaceEmbeddings_Patients_PersonId",
                table: "FaceEmbeddings",
                column: "PersonId",
                principalTable: "Patients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RecognitionLogs_Patients_PersonId",
                table: "RecognitionLogs",
                column: "PersonId",
                principalTable: "Patients",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FaceEmbeddings_Patients_PersonId",
                table: "FaceEmbeddings");

            migrationBuilder.DropForeignKey(
                name: "FK_RecognitionLogs_Patients_PersonId",
                table: "RecognitionLogs");

            migrationBuilder.DropTable(
                name: "Biometrics");

            migrationBuilder.DropTable(
                name: "Visits");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Patients",
                table: "Patients");

            migrationBuilder.DropIndex(
                name: "IX_Patients_IDCard",
                table: "Patients");

            migrationBuilder.DropIndex(
                name: "IX_Patients_Site",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "AddressCode",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "AddressOther",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "AdmissionDate",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "AgeAtEnrolment",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "ConsentDate",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "ConsentGiven",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "DOBDay",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "DOBMonth",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "DOBYear",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "DayAtEnrolment",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "FatherName",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "IDCard",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "LastSync",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "ModifiedBy",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "ModifiedDate",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "MonthAtEnrolment",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "MotherName",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "MotherPID",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "Sex",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "Site",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "SpouseName",
                table: "Patients");

            migrationBuilder.RenameTable(
                name: "Patients",
                newName: "Persons");

            migrationBuilder.RenameColumn(
                name: "FullName",
                table: "Persons",
                newName: "Name");

            migrationBuilder.RenameIndex(
                name: "IX_Patients_IsActive",
                table: "Persons",
                newName: "IX_Persons_IsActive");

            migrationBuilder.RenameIndex(
                name: "IX_Patients_FullName",
                table: "Persons",
                newName: "IX_Persons_Name");

            migrationBuilder.RenameIndex(
                name: "IX_Patients_ExternalId",
                table: "Persons",
                newName: "IX_Persons_ExternalId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Persons",
                table: "Persons",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_FaceEmbeddings_Persons_PersonId",
                table: "FaceEmbeddings",
                column: "PersonId",
                principalTable: "Persons",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RecognitionLogs_Persons_PersonId",
                table: "RecognitionLogs",
                column: "PersonId",
                principalTable: "Persons",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
