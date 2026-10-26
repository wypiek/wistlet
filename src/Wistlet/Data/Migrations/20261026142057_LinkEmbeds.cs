using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wistlet.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkEmbeds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmbedUrls",
                table: "Posts",
                type: "TEXT",
                maxLength: 10000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "LinkPreviews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    SiteName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkPreviews", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LinkPreviews_Url",
                table: "LinkPreviews",
                column: "Url",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkPreviews");

            migrationBuilder.DropColumn(
                name: "EmbedUrls",
                table: "Posts");
        }
    }
}
