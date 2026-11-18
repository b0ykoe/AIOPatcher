using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChangelogsAndAi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UpdatesSnapshot",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ProtectedApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HostUpdateSummaries",
                columns: table => new
                {
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    UpdateCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostUpdateSummaries", x => x.HostId);
                    table.ForeignKey(
                        name: "FK_HostUpdateSummaries_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PackageChangelogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryModel = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    SummaryLanguage = table.Column<string>(type: "TEXT", maxLength: 5, nullable: true),
                    SummaryFrom = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SummaryAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SummaryError = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageChangelogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PackageChangelogs_FetchedAt",
                table: "PackageChangelogs",
                column: "FetchedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PackageChangelogs_Name_Version",
                table: "PackageChangelogs",
                columns: new[] { "Name", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiSettings");

            migrationBuilder.DropTable(
                name: "HostUpdateSummaries");

            migrationBuilder.DropTable(
                name: "PackageChangelogs");

            migrationBuilder.DropColumn(
                name: "UpdatesSnapshot",
                table: "Jobs");
        }
    }
}
