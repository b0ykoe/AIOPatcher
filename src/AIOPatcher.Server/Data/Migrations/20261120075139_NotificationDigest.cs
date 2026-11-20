using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DigestMinutes",
                table: "NotificationChannels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "InstantEvents",
                table: "NotificationChannels",
                type: "INTEGER",
                maxLength: 32,
                nullable: false,
                defaultValue: 32421); // NotificationChannel.ImportantEvents

            migrationBuilder.CreateTable(
                name: "PendingNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Event = table.Column<int>(type: "INTEGER", maxLength: 32, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HostName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SiteName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LinkPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingNotifications_NotificationChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "NotificationChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingNotifications_ChannelId",
                table: "PendingNotifications",
                column: "ChannelId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingNotifications");

            migrationBuilder.DropColumn(
                name: "DigestMinutes",
                table: "NotificationChannels");

            migrationBuilder.DropColumn(
                name: "InstantEvents",
                table: "NotificationChannels");
        }
    }
}
