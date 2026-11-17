using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentUpdateStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AgentUpdateAt",
                table: "Hosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentUpdateBlockedVersion",
                table: "Hosts",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentUpdateFromVersion",
                table: "Hosts",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentUpdateMessage",
                table: "Hosts",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentUpdateOutcome",
                table: "Hosts",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentUpdateToVersion",
                table: "Hosts",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentUpdateAt",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "AgentUpdateBlockedVersion",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "AgentUpdateFromVersion",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "AgentUpdateMessage",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "AgentUpdateOutcome",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "AgentUpdateToVersion",
                table: "Hosts");
        }
    }
}
