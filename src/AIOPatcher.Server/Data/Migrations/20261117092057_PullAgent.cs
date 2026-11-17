using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PullAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Architecture",
                table: "Hosts",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisabledModules",
                table: "Hosts",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProtocolVersion",
                table: "Hosts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ReportedModules",
                table: "Hosts",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    HostId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Module = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentTasks_Hosts_HostId",
                        column: x => x.HostId,
                        principalTable: "Hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_CreatedAt",
                table: "AgentTasks",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_HostId_Status",
                table: "AgentTasks",
                columns: new[] { "HostId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "Architecture",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "DisabledModules",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "ProtocolVersion",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "ReportedModules",
                table: "Hosts");
        }
    }
}
