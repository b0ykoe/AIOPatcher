using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ContainerCompose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComposeFiles",
                table: "Containers",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ComposeFilesFound",
                table: "Containers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ComposeProject",
                table: "Containers",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ComposeService",
                table: "Containers",
                type: "TEXT",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComposeFiles",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "ComposeFilesFound",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "ComposeProject",
                table: "Containers");

            migrationBuilder.DropColumn(
                name: "ComposeService",
                table: "Containers");
        }
    }
}
