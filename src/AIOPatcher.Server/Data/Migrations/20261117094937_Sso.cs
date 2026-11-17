using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Sso : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SsoSubject",
                table: "AdminUsers",
                type: "TEXT",
                maxLength: 600,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SsoSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Authority = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ProtectedClientSecret = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Scopes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UsernameClaim = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    GroupsClaim = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RoleMappingsJson = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    DefaultRole = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    AutoCreateUsers = table.Column<bool>(type: "INTEGER", nullable: false),
                    LinkByUsername = table.Column<bool>(type: "INTEGER", nullable: false),
                    CertificateFingerprint = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SsoSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_SsoSubject",
                table: "AdminUsers",
                column: "SsoSubject",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SsoSettings");

            migrationBuilder.DropIndex(
                name: "IX_AdminUsers_SsoSubject",
                table: "AdminUsers");

            migrationBuilder.DropColumn(
                name: "SsoSubject",
                table: "AdminUsers");
        }
    }
}
