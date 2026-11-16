using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Users : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "AdminUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InviteExpiresAt",
                table: "AdminUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InviteTokenHash",
                table: "AdminUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDisabled",
                table: "AdminUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "AdminUsers",
                type: "TEXT",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "AdminUsers",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Admin");

            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "AdminUsers",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Bestehende Konten bekommen ein zufälliges Sicherheitsmerkmal (alte Sitzungen werden damit ungültig).
            migrationBuilder.Sql("UPDATE \"AdminUsers\" SET \"SecurityStamp\" = lower(hex(randomblob(16)));");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_InviteTokenHash",
                table: "AdminUsers",
                column: "InviteTokenHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AdminUsers_InviteTokenHash",
                table: "AdminUsers");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "AdminUsers");

            migrationBuilder.DropColumn(
                name: "InviteExpiresAt",
                table: "AdminUsers");

            migrationBuilder.DropColumn(
                name: "InviteTokenHash",
                table: "AdminUsers");

            migrationBuilder.DropColumn(
                name: "IsDisabled",
                table: "AdminUsers");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "AdminUsers");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "AdminUsers");

            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "AdminUsers");
        }
    }
}
