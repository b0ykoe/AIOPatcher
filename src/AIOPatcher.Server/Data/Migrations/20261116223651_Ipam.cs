using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Ipam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Hostname",
                table: "IpAddresses",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MacAddress",
                table: "IpAddresses",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Hostname",
                table: "IpAddresses");

            migrationBuilder.DropColumn(
                name: "MacAddress",
                table: "IpAddresses");
        }
    }
}
