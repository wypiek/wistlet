using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wistlet.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExtraPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OwnerId",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_OwnerId",
                table: "Users",
                column: "OwnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Users_OwnerId",
                table: "Users",
                column: "OwnerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Users_OwnerId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_OwnerId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Users");
        }
    }
}
