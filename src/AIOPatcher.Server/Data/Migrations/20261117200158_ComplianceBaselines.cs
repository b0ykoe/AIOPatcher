using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ComplianceBaselines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComplianceBaselines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RulesJson = table.Column<string>(type: "TEXT", nullable: false),
                    MemberTags = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    MemberSiteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IncludeOthers = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplianceBaselines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ComplianceBaselineHosts",
                columns: table => new
                {
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BaselineId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplianceBaselineHosts", x => x.HostId);
                    table.ForeignKey(
                        name: "FK_ComplianceBaselineHosts_ComplianceBaselines_BaselineId",
                        column: x => x.BaselineId,
                        principalTable: "ComplianceBaselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ComplianceBaselineHosts_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceBaselineHosts_BaselineId",
                table: "ComplianceBaselineHosts",
                column: "BaselineId");

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceBaselines_Name",
                table: "ComplianceBaselines",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComplianceBaselineHosts");

            migrationBuilder.DropTable(
                name: "ComplianceBaselines");
        }
    }
}
