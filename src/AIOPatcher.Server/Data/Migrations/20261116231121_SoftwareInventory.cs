using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SoftwareInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Packages",
                table: "Jobs",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InstalledApps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Publisher = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PackageId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AvailableVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstalledApps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstalledApps_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstalledApps_HostId",
                table: "InstalledApps",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_InstalledApps_PackageId",
                table: "InstalledApps",
                column: "PackageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstalledApps");

            migrationBuilder.DropColumn(
                name: "Packages",
                table: "Jobs");
        }
    }
}
