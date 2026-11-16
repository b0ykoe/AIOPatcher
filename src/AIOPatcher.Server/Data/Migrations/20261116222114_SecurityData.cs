using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecurityData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CveCheckedAt",
                table: "PendingUpdates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cves",
                table: "PendingUpdates",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsKev",
                table: "PendingUpdates",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "MaxEpss",
                table: "PendingUpdates",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePackage",
                table: "PendingUpdates",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotifiedKevCves",
                table: "Hosts",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Containers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Image = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ImageId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LocalDigest = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Containers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Containers_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CveInfos",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Epss = table.Column<double>(type: "REAL", nullable: true),
                    EpssPercentile = table.Column<double>(type: "REAL", nullable: true),
                    IsKev = table.Column<bool>(type: "INTEGER", nullable: false),
                    KevDateAdded = table.Column<DateTime>(type: "TEXT", nullable: true),
                    KevName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    KevRansomware = table.Column<bool>(type: "INTEGER", nullable: false),
                    EpssUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CveInfos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImageChecks",
                columns: table => new
                {
                    Reference = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    RemoteDigest = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CheckedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    NotifiedDigest = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageChecks", x => x.Reference);
                });

            migrationBuilder.CreateTable(
                name: "VulnLookups",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    Cves = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VulnLookups", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingUpdates_CveCheckedAt",
                table: "PendingUpdates",
                column: "CveCheckedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Containers_HostId",
                table: "Containers",
                column: "HostId");

            migrationBuilder.CreateIndex(
                name: "IX_CveInfos_IsKev",
                table: "CveInfos",
                column: "IsKev");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Containers");

            migrationBuilder.DropTable(
                name: "CveInfos");

            migrationBuilder.DropTable(
                name: "ImageChecks");

            migrationBuilder.DropTable(
                name: "VulnLookups");

            migrationBuilder.DropIndex(
                name: "IX_PendingUpdates_CveCheckedAt",
                table: "PendingUpdates");

            migrationBuilder.DropColumn(
                name: "CveCheckedAt",
                table: "PendingUpdates");

            migrationBuilder.DropColumn(
                name: "Cves",
                table: "PendingUpdates");

            migrationBuilder.DropColumn(
                name: "IsKev",
                table: "PendingUpdates");

            migrationBuilder.DropColumn(
                name: "MaxEpss",
                table: "PendingUpdates");

            migrationBuilder.DropColumn(
                name: "SourcePackage",
                table: "PendingUpdates");

            migrationBuilder.DropColumn(
                name: "NotifiedKevCves",
                table: "Hosts");
        }
    }
}
