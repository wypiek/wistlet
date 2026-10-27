using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wistlet.Data.Migrations
{
    /// <inheritdoc />
    public partial class PauseAnimatedImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PauseAnimatedImages",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PauseAnimatedImages",
                table: "Users");
        }
    }
}
