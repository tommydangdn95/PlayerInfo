using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlayerInfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SwitchToEspn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Players, injuries and fixtures came from API-Football and have no ESPN ids: clear them
            // (they are re-synced from ESPN), together with keywords/article links pointing at players.
            migrationBuilder.Sql("""
                DELETE FROM "ArticleEntities" WHERE "EntityType" = 'Player';
                DELETE FROM "WatchlistAliases" WHERE "EntityType" = 'Player';
                DELETE FROM "PlayerInjuries";
                DELETE FROM "Fixtures";
                DELETE FROM "Players";
                """);

            migrationBuilder.DropIndex(
                name: "IX_Teams_ExternalApiId",
                table: "Teams");

            migrationBuilder.DropIndex(
                name: "IX_Players_ExternalApiId",
                table: "Players");

            migrationBuilder.DropIndex(
                name: "IX_PlayerInjuries_PlayerExternalId_ReportedDateUtc",
                table: "PlayerInjuries");

            migrationBuilder.DropIndex(
                name: "IX_Fixtures_ExternalFixtureId",
                table: "Fixtures");

            migrationBuilder.DropColumn(
                name: "ExternalApiId",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "PlayerExternalId",
                table: "PlayerInjuries");

            migrationBuilder.DropColumn(
                name: "TeamExternalId",
                table: "PlayerInjuries");

            migrationBuilder.DropColumn(
                name: "ExternalFixtureId",
                table: "Fixtures");

            migrationBuilder.DropColumn(
                name: "LeagueExternalId",
                table: "Fixtures");

            migrationBuilder.DropColumn(
                name: "LeagueExternalId",
                table: "Teams");

            // Existing teams were added with API-Football ids: keep them as the (optional) transfers link.
            migrationBuilder.RenameColumn(
                name: "ExternalApiId",
                table: "Teams",
                newName: "ApiFootballId");

            migrationBuilder.AlterColumn<int>(
                name: "ApiFootballId",
                table: "Teams",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "EspnId",
                table: "Teams",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeagueSlug",
                table: "Teams",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EspnId",
                table: "Players",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PlayerEspnId",
                table: "PlayerInjuries",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TeamEspnId",
                table: "PlayerInjuries",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "StatusShort",
                table: "Fixtures",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "AwayTeamEspnId",
                table: "Fixtures",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EspnEventId",
                table: "Fixtures",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HomeTeamEspnId",
                table: "Fixtures",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeagueSlug",
                table: "Fixtures",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_ApiFootballId",
                table: "Teams",
                column: "ApiFootballId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Teams_EspnId",
                table: "Teams",
                column: "EspnId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Players_EspnId",
                table: "Players",
                column: "EspnId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerInjuries_PlayerEspnId_ReportedDateUtc",
                table: "PlayerInjuries",
                columns: new[] { "PlayerEspnId", "ReportedDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Fixtures_EspnEventId",
                table: "Fixtures",
                column: "EspnEventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ESPN-sourced rows cannot be mapped back to API-Football ids.
            migrationBuilder.Sql("""
                DELETE FROM "ArticleEntities" WHERE "EntityType" = 'Player';
                DELETE FROM "WatchlistAliases" WHERE "EntityType" = 'Player';
                DELETE FROM "PlayerInjuries";
                DELETE FROM "Fixtures";
                DELETE FROM "Players";
                """);

            migrationBuilder.DropIndex(
                name: "IX_Teams_ApiFootballId",
                table: "Teams");

            migrationBuilder.DropIndex(
                name: "IX_Teams_EspnId",
                table: "Teams");

            migrationBuilder.DropIndex(
                name: "IX_Players_EspnId",
                table: "Players");

            migrationBuilder.DropIndex(
                name: "IX_PlayerInjuries_PlayerEspnId_ReportedDateUtc",
                table: "PlayerInjuries");

            migrationBuilder.DropIndex(
                name: "IX_Fixtures_EspnEventId",
                table: "Fixtures");

            migrationBuilder.DropColumn(
                name: "EspnId",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "LeagueSlug",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "EspnId",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "PlayerEspnId",
                table: "PlayerInjuries");

            migrationBuilder.DropColumn(
                name: "TeamEspnId",
                table: "PlayerInjuries");

            migrationBuilder.DropColumn(
                name: "AwayTeamEspnId",
                table: "Fixtures");

            migrationBuilder.DropColumn(
                name: "EspnEventId",
                table: "Fixtures");

            migrationBuilder.DropColumn(
                name: "HomeTeamEspnId",
                table: "Fixtures");

            migrationBuilder.DropColumn(
                name: "LeagueSlug",
                table: "Fixtures");

            // Teams without an API-Football id get a unique negative placeholder (the old column was required).
            migrationBuilder.Sql("""UPDATE "Teams" SET "ApiFootballId" = -"Id" WHERE "ApiFootballId" IS NULL;""");

            migrationBuilder.AlterColumn<int>(
                name: "ApiFootballId",
                table: "Teams",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.RenameColumn(
                name: "ApiFootballId",
                table: "Teams",
                newName: "ExternalApiId");

            migrationBuilder.AddColumn<int>(
                name: "LeagueExternalId",
                table: "Teams",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExternalApiId",
                table: "Players",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PlayerExternalId",
                table: "PlayerInjuries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TeamExternalId",
                table: "PlayerInjuries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "StatusShort",
                table: "Fixtures",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AddColumn<int>(
                name: "ExternalFixtureId",
                table: "Fixtures",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LeagueExternalId",
                table: "Fixtures",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Teams_ExternalApiId",
                table: "Teams",
                column: "ExternalApiId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Players_ExternalApiId",
                table: "Players",
                column: "ExternalApiId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerInjuries_PlayerExternalId_ReportedDateUtc",
                table: "PlayerInjuries",
                columns: new[] { "PlayerExternalId", "ReportedDateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Fixtures_ExternalFixtureId",
                table: "Fixtures",
                column: "ExternalFixtureId",
                unique: true);
        }
    }
}
