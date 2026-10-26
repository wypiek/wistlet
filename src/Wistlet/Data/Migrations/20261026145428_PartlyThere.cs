using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wistlet.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartlyThere : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FeedHideAsks",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "FeedHideReplies",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "FeedHideShares",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "FeedSeenUpTo",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PinnedTags",
                table: "Users",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Row",
                table: "PostAttachments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "TagSynonyms",
                columns: table => new
                {
                    Alias = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Canonical = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagSynonyms", x => x.Alias);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TagSynonyms_Canonical",
                table: "TagSynonyms",
                column: "Canonical");

            // Existing posts keep the layout they had: one image alone, three as one + two, otherwise pairs.
            migrationBuilder.Sql("""
                UPDATE PostAttachments SET Row = CASE
                    WHEN (SELECT COUNT(*) FROM PostAttachments o WHERE o.PostId = PostAttachments.PostId) = 3
                        THEN CASE WHEN Position = 0 THEN 0 ELSE 1 END
                    ELSE Position / 2
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TagSynonyms");

            migrationBuilder.DropColumn(
                name: "FeedHideAsks",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FeedHideReplies",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FeedHideShares",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FeedSeenUpTo",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PinnedTags",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Row",
                table: "PostAttachments");
        }
    }
}
