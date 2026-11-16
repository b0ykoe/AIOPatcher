using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Integrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequireBackupWithinHours",
                table: "RunbookWaves",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SnapshotBeforePatch",
                table: "RunbookWaves",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RequireBackupWithinHours",
                table: "RunbookRunWaves",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SnapshotBeforePatch",
                table: "RunbookRunWaves",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PendingTask",
                table: "RunbookRunItems",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PrepareDeadline",
                table: "RunbookRunItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotName",
                table: "RunbookRunItems",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SnapshotTakenAt",
                table: "RunbookRunItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Hosts",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IntegrationId",
                table: "Hosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ProtectedSecret = table.Column<string>(type: "TEXT", nullable: false),
                    TlsFingerprintSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    KeepSnapshots = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastCheckAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    StatusJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Integrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Integrations_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_IntegrationId",
                table: "Hosts",
                column: "IntegrationId");

            migrationBuilder.CreateIndex(
                name: "IX_Integrations_SiteId",
                table: "Integrations",
                column: "SiteId");

            migrationBuilder.AddForeignKey(
                name: "FK_Hosts_Integrations_IntegrationId",
                table: "Hosts",
                column: "IntegrationId",
                principalTable: "Integrations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Hosts_Integrations_IntegrationId",
                table: "Hosts");

            migrationBuilder.DropTable(
                name: "Integrations");

            migrationBuilder.DropIndex(
                name: "IX_Hosts_IntegrationId",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "RequireBackupWithinHours",
                table: "RunbookWaves");

            migrationBuilder.DropColumn(
                name: "SnapshotBeforePatch",
                table: "RunbookWaves");

            migrationBuilder.DropColumn(
                name: "RequireBackupWithinHours",
                table: "RunbookRunWaves");

            migrationBuilder.DropColumn(
                name: "SnapshotBeforePatch",
                table: "RunbookRunWaves");

            migrationBuilder.DropColumn(
                name: "PendingTask",
                table: "RunbookRunItems");

            migrationBuilder.DropColumn(
                name: "PrepareDeadline",
                table: "RunbookRunItems");

            migrationBuilder.DropColumn(
                name: "SnapshotName",
                table: "RunbookRunItems");

            migrationBuilder.DropColumn(
                name: "SnapshotTakenAt",
                table: "RunbookRunItems");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "IntegrationId",
                table: "Hosts");
        }
    }
}
