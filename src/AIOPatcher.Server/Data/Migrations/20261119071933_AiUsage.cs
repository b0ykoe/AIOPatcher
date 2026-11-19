using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiUsage",
                columns: table => new
                {
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Requests = table.Column<int>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsage", x => x.Day);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiUsage");
        }
    }
}
