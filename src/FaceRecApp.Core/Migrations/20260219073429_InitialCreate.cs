using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FaceRecApp.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Persons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExternalId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TotalRecognitions = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Persons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FaceEmbeddings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PersonId = table.Column<int>(type: "int", nullable: false),
                    Embedding = table.Column<string>(type: "vector(512)", nullable: false),
                    FaceThumbnail = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CaptureAngle = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    QualityScore = table.Column<float>(type: "real", nullable: true),
                    CapturedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceEmbeddings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FaceEmbeddings_Persons_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecognitionLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PersonId = table.Column<int>(type: "int", nullable: true),
                    Distance = table.Column<float>(type: "real", nullable: false),
                    WasRecognized = table.Column<bool>(type: "bit", nullable: false),
                    PassedLiveness = table.Column<bool>(type: "bit", nullable: false),
                    StationId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecognitionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecognitionLogs_Persons_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FaceEmbeddings_PersonId",
                table: "FaceEmbeddings",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Persons_ExternalId",
                table: "Persons",
                column: "ExternalId",
                unique: true,
                filter: "[ExternalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Persons_IsActive",
                table: "Persons",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Persons_Name",
                table: "Persons",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionLogs_PersonId",
                table: "RecognitionLogs",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionLogs_Timestamp",
                table: "RecognitionLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_RecognitionLogs_WasRecognized",
                table: "RecognitionLogs",
                column: "WasRecognized");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FaceEmbeddings");

            migrationBuilder.DropTable(
                name: "RecognitionLogs");

            migrationBuilder.DropTable(
                name: "Persons");
        }
    }
}
