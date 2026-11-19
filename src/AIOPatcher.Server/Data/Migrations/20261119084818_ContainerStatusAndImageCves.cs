using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ContainerStatusAndImageCves : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseImage",
                table: "Containers",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContainerId",
                table: "Containers",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExitCode",
                table: "Containers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FinishedAt",
                table: "Containers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Health",
                table: "Containers",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LocalImage",
                table: "Containers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RestartPolicy",
                table: "Containers",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAt",
                table: "Containers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ImagePackageSets",
                columns: table => new
                {
                    ImageId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    OsId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    OsVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Format = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Ecosystem = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Packages = table.Column<string>(type: "TEXT", nullable: false),
                    PackageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CollectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScannedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NotifiedKevCves = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImagePackageSets", x => x.ImageId);
                });

            migrationBuilder.CreateTable(
                name: "ImageVulnerabilities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ImageId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CveId = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Package = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    InstalledVersion = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    FoundAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageVulnerabilities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImageVulnerabilities_ImageId",
                table: "ImageVulnerabilities",
                column: "ImageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImagePackageSets");

            migrationBuilder.DropTable(
                name: "ImageVulnerabilities");

            migrationBuilder.DropColumn(
                name: "BaseImage",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "ContainerId",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "ExitCode",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "FinishedAt",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "Health",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "LocalImage",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "RestartPolicy",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "Containers");
        }
    }
}
