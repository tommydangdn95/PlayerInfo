using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlayerInfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNewsIsHot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsHot",
                table: "NewsArticles",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsHot",
                table: "NewsArticles");
        }
    }
}
