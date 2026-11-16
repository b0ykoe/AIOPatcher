using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PatchRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "Hosts",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PackageHolds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageHolds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackageHolds_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PackageHolds_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PatchFreezes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StartsAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatchFreezes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatchFreezes_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PackageHolds_HostId",
                table: "PackageHolds",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_PackageHolds_SiteId",
                table: "PackageHolds",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_PatchFreezes_EndsAt",
                table: "PatchFreezes",
                column: "EndsAt");

            migrationBuilder.CreateIndex(
                name: "IX_PatchFreezes_SiteId",
                table: "PatchFreezes",
                column: "SiteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PackageHolds");

            migrationBuilder.DropTable(
                name: "PatchFreezes");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Hosts");
        }
    }
}
