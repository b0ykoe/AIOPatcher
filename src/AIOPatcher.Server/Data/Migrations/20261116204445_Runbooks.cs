using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Runbooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Runbooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    FailurePolicy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ScheduleEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScheduleDays = table.Column<int>(type: "INTEGER", maxLength: 32, nullable: false),
                    ScheduleStart = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    ScheduleDurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    LastWindowStart = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastWindowNote = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runbooks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunbookRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunbookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CurrentWave = table.Column<int>(type: "INTEGER", nullable: false),
                    NotAfter = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailurePolicy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    AcknowledgedWave = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunbookRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunbookRuns_Runbooks_RunbookId",
                        column: x => x.RunbookId,
                        principalTable: "Runbooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunbookWaves",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunbookId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AllowReboot = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequireApproval = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunbookWaves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunbookWaves_Runbooks_RunbookId",
                        column: x => x.RunbookId,
                        principalTable: "Runbooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunbookRunItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WaveOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HostName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VerifyDeadline = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunbookRunItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunbookRunItems_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RunbookRunItems_RunbookRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "RunbookRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunbookRunWaves",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AllowReboot = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequireApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    Approved = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunbookRunWaves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunbookRunWaves_RunbookRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "RunbookRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunbookWaveHosts",
                columns: table => new
                {
                    WaveId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunbookWaveHosts", x => new { x.WaveId, x.HostId });
                    table.ForeignKey(
                        name: "FK_RunbookWaveHosts_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RunbookWaveHosts_RunbookWaves_WaveId",
                        column: x => x.WaveId,
                        principalTable: "RunbookWaves",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RunbookRunItems_HostId",
                table: "RunbookRunItems",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_RunbookRunItems_JobId",
                table: "RunbookRunItems",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_RunbookRunItems_RunId_WaveOrder",
                table: "RunbookRunItems",
                columns: new[] { "RunId", "WaveOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_RunbookRunWaves_RunId",
                table: "RunbookRunWaves",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_RunbookRuns_CreatedAt",
                table: "RunbookRuns",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RunbookRuns_RunbookId_Status",
                table: "RunbookRuns",
                columns: new[] { "RunbookId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RunbookWaveHosts_HostId",
                table: "RunbookWaveHosts",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_RunbookWaves_RunbookId_Order",
                table: "RunbookWaves",
                columns: new[] { "RunbookId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_Runbooks_Name",
                table: "Runbooks",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunbookRunItems");

            migrationBuilder.DropTable(
                name: "RunbookRunWaves");

            migrationBuilder.DropTable(
                name: "RunbookWaveHosts");

            migrationBuilder.DropTable(
                name: "RunbookRuns");

            migrationBuilder.DropTable(
                name: "RunbookWaves");

            migrationBuilder.DropTable(
                name: "Runbooks");
        }
    }
}
