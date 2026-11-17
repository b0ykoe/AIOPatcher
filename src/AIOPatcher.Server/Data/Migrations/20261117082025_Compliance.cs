using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Compliance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FirewallVersionId",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Payload",
                table: "Jobs",
                type: "TEXT",
                maxLength: 64000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedSecret",
                table: "Jobs",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FirewallAppliedAt",
                table: "Hosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FirewallVersionId",
                table: "Hosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FirewallProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FirewallProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HostCompliance",
                columns: table => new
                {
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CollectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    FirewallProduct = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FirewallEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirewallManageable = table.Column<bool>(type: "INTEGER", nullable: false),
                    AppliedFirewallHash = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    RemoteAccessIssues = table.Column<int>(type: "INTEGER", nullable: false),
                    Users = table.Column<int>(type: "INTEGER", nullable: false),
                    Admins = table.Column<int>(type: "INTEGER", nullable: false),
                    BuiltInAdminActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostCompliance", x => x.HostId);
                    table.ForeignKey(
                        name: "FK_HostCompliance_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FirewallProfileVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FirewallProfileVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FirewallProfileVersions_FirewallProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "FirewallProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FirewallProfileVersions_Hash",
                table: "FirewallProfileVersions",
                column: "Hash");

            migrationBuilder.CreateIndex(
                name: "IX_FirewallProfileVersions_ProfileId_Number",
                table: "FirewallProfileVersions",
                columns: new[] { "ProfileId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FirewallProfiles_Name",
                table: "FirewallProfiles",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FirewallProfileVersions");

            migrationBuilder.DropTable(
                name: "HostCompliance");

            migrationBuilder.DropTable(
                name: "FirewallProfiles");

            migrationBuilder.DropColumn(
                name: "FirewallVersionId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "Payload",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ProtectedSecret",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "FirewallAppliedAt",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "FirewallVersionId",
                table: "Hosts");
        }
    }
}
