using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIOPatcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SiteSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RemoteSiteId",
                table: "NotificationChannels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FederationPeers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CertificateThumbprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CertificateExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PendingCertificateThumbprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PendingCertificateExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeenIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FederationPeers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RemoteInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    CaFingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CaCertificatePem = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    ProtectedKey = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    CertificatePem = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    CertificateExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Capabilities = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteInstances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerIdentity",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    InstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerIdentity", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShareInvites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Data = table.Column<int>(type: "INTEGER", maxLength: 32, nullable: false),
                    ShareDays = table.Column<int>(type: "INTEGER", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    BurnedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UsedByPeerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShareInvites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShareInvites_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SiteShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Data = table.Column<int>(type: "INTEGER", maxLength: 32, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    InviteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAccessAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SiteShares_FederationPeers_PeerId",
                        column: x => x.PeerId,
                        principalTable: "FederationPeers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SiteShares_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RemoteSites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RemoteInstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShareId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RemoteSiteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LocalName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Level = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Data = table.Column<int>(type: "INTEGER", maxLength: 32, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: true),
                    SnapshotAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    JobsCheckedUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteSites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemoteSites_RemoteInstances_RemoteInstanceId",
                        column: x => x.RemoteInstanceId,
                        principalTable: "RemoteInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationChannels_RemoteSiteId",
                table: "NotificationChannels",
                column: "RemoteSiteId");

            migrationBuilder.CreateIndex(
                name: "IX_FederationPeers_CertificateThumbprint",
                table: "FederationPeers",
                column: "CertificateThumbprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FederationPeers_PendingCertificateThumbprint",
                table: "FederationPeers",
                column: "PendingCertificateThumbprint");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSites_RemoteInstanceId",
                table: "RemoteSites",
                column: "RemoteInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSites_ShareId",
                table: "RemoteSites",
                column: "ShareId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShareInvites_SiteId",
                table: "ShareInvites",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_SiteShares_PeerId_SiteId",
                table: "SiteShares",
                columns: new[] { "PeerId", "SiteId" });

            migrationBuilder.CreateIndex(
                name: "IX_SiteShares_SiteId",
                table: "SiteShares",
                column: "SiteId");

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationChannels_RemoteSites_RemoteSiteId",
                table: "NotificationChannels",
                column: "RemoteSiteId",
                principalTable: "RemoteSites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NotificationChannels_RemoteSites_RemoteSiteId",
                table: "NotificationChannels");

            migrationBuilder.DropTable(
                name: "RemoteSites");

            migrationBuilder.DropTable(
                name: "ServerIdentity");

            migrationBuilder.DropTable(
                name: "ShareInvites");

            migrationBuilder.DropTable(
                name: "SiteShares");

            migrationBuilder.DropTable(
                name: "RemoteInstances");

            migrationBuilder.DropTable(
                name: "FederationPeers");

            migrationBuilder.DropIndex(
                name: "IX_NotificationChannels_RemoteSiteId",
                table: "NotificationChannels");

            migrationBuilder.DropColumn(
                name: "RemoteSiteId",
                table: "NotificationChannels");
        }
    }
}
