using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class TokensAndRenewal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CertificateExpiryNotifiedAt",
                table: "Hosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PendingCertificateExpiresAt",
                table: "Hosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingCertificateThumbprint",
                table: "Hosts",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApiTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Hosts_PendingCertificateThumbprint",
                table: "Hosts",
                column: "PendingCertificateThumbprint");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_TokenHash",
                table: "ApiTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiTokens");

            migrationBuilder.DropIndex(
                name: "IX_Hosts_PendingCertificateThumbprint",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "CertificateExpiryNotifiedAt",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "PendingCertificateExpiresAt",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "PendingCertificateThumbprint",
                table: "Hosts");
        }
    }
}
