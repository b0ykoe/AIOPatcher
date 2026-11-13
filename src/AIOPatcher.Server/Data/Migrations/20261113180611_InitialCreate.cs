using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedTotpSecret = table.Column<string>(type: "TEXT", nullable: false),
                    IsSetupComplete = table.Column<bool>(type: "INTEGER", nullable: false),
                    FailedLoginCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LockoutUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastTotpTimeStep = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Details = table.Column<string>(type: "TEXT", nullable: true),
                    RemoteIp = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    AllowedAgentCidrs = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnrollmentTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UsedByHostId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrollmentTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnrollmentTokens_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Hosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    OsFamily = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OsName = table.Column<string>(type: "TEXT", nullable: true),
                    OsVersion = table.Column<string>(type: "TEXT", nullable: true),
                    KernelVersion = table.Column<string>(type: "TEXT", nullable: true),
                    AgentVersion = table.Column<string>(type: "TEXT", nullable: true),
                    EnrolledAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastScanAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Uptime = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    RebootRequired = table.Column<bool>(type: "INTEGER", nullable: false),
                    NativeAutoUpdatesEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CertificateThumbprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CertificateExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsRevoked = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoPatchEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoPatchScope = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AutoRebootAllowed = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaintenanceDays = table.Column<int>(type: "INTEGER", maxLength: 32, nullable: false),
                    MaintenanceStart = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    MaintenanceDurationMinutes = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Hosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Hosts_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Subnets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cidr = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subnets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subnets_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AllowReboot = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    RebootPerformed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Jobs_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingUpdates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentVersion = table.Column<string>(type: "TEXT", nullable: true),
                    AvailableVersion = table.Column<string>(type: "TEXT", nullable: true),
                    IsSecurity = table.Column<bool>(type: "INTEGER", nullable: false),
                    Classification = table.Column<string>(type: "TEXT", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingUpdates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingUpdates_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IpAddresses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubnetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    PrefixLength = table.Column<int>(type: "INTEGER", nullable: true),
                    InterfaceName = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpAddresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IpAddresses_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IpAddresses_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IpAddresses_Subnets_SubnetId",
                        column: x => x.SubnetId,
                        principalTable: "Subnets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "JobLogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobLogEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobLogEntries_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_Username",
                table: "AdminUsers",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_Timestamp",
                table: "AuditEntries",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentTokens_SiteId",
                table: "EnrollmentTokens",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentTokens_TokenHash",
                table: "EnrollmentTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_CertificateThumbprint",
                table: "Hosts",
                column: "CertificateThumbprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_SiteId_Name",
                table: "Hosts",
                columns: new[] { "SiteId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_IpAddresses_HostId",
                table: "IpAddresses",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_IpAddresses_SiteId_Address",
                table: "IpAddresses",
                columns: new[] { "SiteId", "Address" });

            migrationBuilder.CreateIndex(
                name: "IX_IpAddresses_SubnetId",
                table: "IpAddresses",
                column: "SubnetId");

            migrationBuilder.CreateIndex(
                name: "IX_JobLogEntries_JobId_Id",
                table: "JobLogEntries",
                columns: new[] { "JobId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CreatedAt",
                table: "Jobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_HostId_Status",
                table: "Jobs",
                columns: new[] { "HostId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingUpdates_HostId",
                table: "PendingUpdates",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_Sites_Name",
                table: "Sites",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subnets_SiteId_Cidr",
                table: "Subnets",
                columns: new[] { "SiteId", "Cidr" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminUsers");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "EnrollmentTokens");

            migrationBuilder.DropTable(
                name: "IpAddresses");

            migrationBuilder.DropTable(
                name: "JobLogEntries");

            migrationBuilder.DropTable(
                name: "PendingUpdates");

            migrationBuilder.DropTable(
                name: "Subnets");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "Hosts");

            migrationBuilder.DropTable(
                name: "Sites");
        }
    }
}
