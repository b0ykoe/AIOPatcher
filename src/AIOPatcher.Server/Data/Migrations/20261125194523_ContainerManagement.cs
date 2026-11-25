using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ContainerManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Result",
                table: "Jobs",
                type: "TEXT",
                maxLength: 64000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DockerDisk",
                table: "Hosts",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Result",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "DockerDisk",
                table: "Hosts");
        }
    }
}
