using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PlayerInfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStoriesAndSourceTiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimType",
                table: "NewsArticles",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Importance",
                table: "NewsArticles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SourceTier",
                table: "NewsArticles",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<long>(
                name: "StoryId",
                table: "NewsArticles",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NewsSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Pattern = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Tier = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Stories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    HeadlineVi = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SummaryVi = table.Column<string>(type: "text", nullable: true),
                    Topic = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Importance = table.Column<int>(type: "integer", nullable: false),
                    Confidence = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceCount = table.Column<int>(type: "integer", nullable: false),
                    BestTier = table.Column<int>(type: "integer", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NotifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NewsArticles_StoryId",
                table: "NewsArticles",
                column: "StoryId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsSources_Pattern",
                table: "NewsSources",
                column: "Pattern",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Stories_LastUpdatedUtc",
                table: "Stories",
                column: "LastUpdatedUtc");

            migrationBuilder.AddForeignKey(
                name: "FK_NewsArticles_Stories_StoryId",
                table: "NewsArticles",
                column: "StoryId",
                principalTable: "Stories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Old categories -> new topics.
            migrationBuilder.Sql("""UPDATE "NewsArticles" SET "Category" = 'Match' WHERE "Category" = 'MatchPerformance';""");
            migrationBuilder.Sql("""UPDATE "NewsArticles" SET "Category" = 'Other' WHERE "Category" = 'OffPitch';""");

            // Importance from the old hot flag for articles Gemini already judged.
            migrationBuilder.Sql("""UPDATE "NewsArticles" SET "Importance" = CASE WHEN "IsHot" THEN 4 ELSE 2 END WHERE "IsAiProcessed" AND "IsRelevant";""");

            // Let Gemini group the last two days of news into stories (already-sent articles are not re-sent).
            migrationBuilder.Sql("""
                UPDATE "NewsArticles" SET "IsAiProcessed" = FALSE
                WHERE "IsRelevant" AND "StoryId" IS NULL AND "PublishedAtUtc" > now() - interval '2 days';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NewsArticles_Stories_StoryId",
                table: "NewsArticles");

            migrationBuilder.DropTable(
                name: "NewsSources");

            migrationBuilder.DropTable(
                name: "Stories");

            migrationBuilder.DropIndex(
                name: "IX_NewsArticles_StoryId",
                table: "NewsArticles");

            migrationBuilder.DropColumn(
                name: "ClaimType",
                table: "NewsArticles");

            migrationBuilder.DropColumn(
                name: "Importance",
                table: "NewsArticles");

            migrationBuilder.DropColumn(
                name: "SourceTier",
                table: "NewsArticles");

            migrationBuilder.DropColumn(
                name: "StoryId",
                table: "NewsArticles");
        }
    }
}
