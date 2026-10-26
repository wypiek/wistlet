using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wistlet.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveImagesToAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Posts had at most one image; it becomes the post's first attachment.
            migrationBuilder.Sql("""
                INSERT INTO PostAttachments (PostId, Path, AltText, Position)
                SELECT Id, ImagePath, '', 0 FROM Posts WHERE ImagePath IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
