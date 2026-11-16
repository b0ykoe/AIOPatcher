using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OfflineNotifiedAt",
                table: "Hosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NotificationChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Events = table.Column<int>(type: "INTEGER", maxLength: 32, nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProtectedConfig = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationChannels_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationChannels_SiteId",
                table: "NotificationChannels",
                column: "SiteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationChannels");

            migrationBuilder.DropColumn(
                name: "OfflineNotifiedAt",
                table: "Hosts");
        }
    }
}
