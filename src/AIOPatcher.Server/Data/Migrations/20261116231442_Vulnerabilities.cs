using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Vulnerabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostVulnerabilities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CveId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Package = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    InstalledVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    FoundAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostVulnerabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HostVulnerabilities_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostVulnerabilities_CveId",
                table: "HostVulnerabilities",
                column: "CveId");

            migrationBuilder.CreateIndex(
                name: "IX_HostVulnerabilities_HostId",
                table: "HostVulnerabilities",
                column: "HostId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostVulnerabilities");
        }
    }
}
