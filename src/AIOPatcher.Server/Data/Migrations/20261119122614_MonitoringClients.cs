using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class MonitoringClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MonitoringChangesAllowed",
                table: "Hosts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MonitoringScannedAt",
                table: "Hosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MonitoringClients",
                columns: table => new
                {
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Product = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Variant = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    ServiceRunning = table.Column<bool>(type: "INTEGER", nullable: false),
                    Addresses = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Connected = table.Column<bool>(type: "INTEGER", nullable: true),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CanConfigure = table.Column<bool>(type: "INTEGER", nullable: false),
                    Held = table.Column<bool>(type: "INTEGER", nullable: false),
                    SeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DisconnectedSince = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringClients", x => new { x.HostId, x.Product });
                    table.ForeignKey(
                        name: "FK_MonitoringClients_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MonitoringTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Product = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    Port = table.Column<int>(type: "INTEGER", nullable: true),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Enrollment = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    EnrollmentPort = table.Column<int>(type: "INTEGER", nullable: true),
                    Groups = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ManagerVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    SiteUrl = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ReceiverPort = table.Column<int>(type: "INTEGER", nullable: true),
                    RegistrationUser = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    OnlyFrom = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LegacyPackageUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TlsFingerprintSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SiteVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ProtectedSecret = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonitoringTargets_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringTargets_Product_SiteId",
                table: "MonitoringTargets",
                columns: new[] { "Product", "SiteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringTargets_SiteId",
                table: "MonitoringTargets",
                column: "SiteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonitoringClients");

            migrationBuilder.DropTable(
                name: "MonitoringTargets");

            migrationBuilder.DropColumn(
                name: "MonitoringChangesAllowed",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "MonitoringScannedAt",
                table: "Hosts");
        }
    }
}
