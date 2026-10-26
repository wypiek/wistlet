using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wistlet.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillCommentRoots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing comments (replies) get the top-level post of their thread.
            migrationBuilder.Sql("""
                WITH RECURSIVE thread(Id, Root) AS (
                    SELECT Id, Id FROM Posts WHERE ParentId IS NULL
                    UNION ALL
                    SELECT p.Id, t.Root FROM Posts p JOIN thread t ON p.ParentId = t.Id
                )
                UPDATE Posts SET RootId = (SELECT Root FROM thread WHERE thread.Id = Posts.Id)
                WHERE ParentId IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
