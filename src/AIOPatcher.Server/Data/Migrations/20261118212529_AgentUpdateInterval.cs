using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentUpdateInterval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AgentUpdateMinutes",
                table: "ScanSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 360); // vorhandene Einstellungen behalten den bisherigen Abstand (6 Stunden)
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentUpdateMinutes",
                table: "ScanSettings");
        }
    }
}
