using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PatchPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IncludeOthers",
                table: "RunbookWaves",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "MemberSiteId",
                table: "RunbookWaves",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MemberTags",
                table: "RunbookWaves",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PauseAfterMinutes",
                table: "RunbookWaves",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResumeAt",
                table: "RunbookRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PauseAfterMinutes",
                table: "RunbookRunWaves",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncludeOthers",
                table: "RunbookWaves");

            migrationBuilder.DropColumn(
                name: "MemberSiteId",
                table: "RunbookWaves");

            migrationBuilder.DropColumn(
                name: "MemberTags",
                table: "RunbookWaves");

            migrationBuilder.DropColumn(
                name: "PauseAfterMinutes",
                table: "RunbookWaves");

            migrationBuilder.DropColumn(
                name: "ResumeAt",
                table: "RunbookRuns");

            migrationBuilder.DropColumn(
                name: "PauseAfterMinutes",
                table: "RunbookRunWaves");
        }
    }
}
