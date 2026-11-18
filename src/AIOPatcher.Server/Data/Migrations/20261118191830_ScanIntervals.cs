using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScanIntervals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ImageCheckHours",
                table: "Hosts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UpdateScanHours",
                table: "Hosts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScanSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdateScanHours = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageCheckHours = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScanSettings");

            migrationBuilder.DropColumn(
                name: "ImageCheckHours",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "UpdateScanHours",
                table: "Hosts");
        }
    }
}
