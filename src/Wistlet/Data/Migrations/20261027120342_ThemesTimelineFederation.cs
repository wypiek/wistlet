using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wistlet.Data.Migrations
{
    /// <inheritdoc />
    public partial class ThemesTimelineFederation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Domain",
                table: "Users",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RemoteFetchedAt",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Theme",
                table: "Users",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TimelineMode",
                table: "Users",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RemoteId",
                table: "Posts",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FederationDeliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FailedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FederationDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RemoteInstances",
                columns: table => new
                {
                    Domain = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PublicKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsBlocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteInstances", x => x.Domain);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Domain",
                table: "Users",
                column: "Domain");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_RemoteId",
                table: "Posts",
                column: "RemoteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FederationDeliveries_FailedAt_NextAttemptAt",
                table: "FederationDeliveries",
                columns: new[] { "FailedAt", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FederationDeliveries");

            migrationBuilder.DropTable(
                name: "RemoteInstances");

            migrationBuilder.DropIndex(
                name: "IX_Users_Domain",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Posts_RemoteId",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "Domain",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RemoteFetchedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Theme",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TimelineMode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RemoteId",
                table: "Posts");
        }
    }
}
