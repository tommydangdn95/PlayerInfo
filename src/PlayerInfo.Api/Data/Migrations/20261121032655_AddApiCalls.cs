using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PlayerInfo.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiCalls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Provider = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Endpoint = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    ResponseBytes = table.Column<long>(type: "bigint", nullable: true),
                    PromptTokens = table.Column<int>(type: "integer", nullable: false),
                    OutputTokens = table.Column<int>(type: "integer", nullable: false),
                    ThoughtTokens = table.Column<int>(type: "integer", nullable: false),
                    TotalTokens = table.Column<int>(type: "integer", nullable: false),
                    ArticleCount = table.Column<int>(type: "integer", nullable: false),
                    JobId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiCalls", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiCalls_AtUtc",
                table: "ApiCalls",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ApiCalls_Provider_Model_AtUtc",
                table: "ApiCalls",
                columns: new[] { "Provider", "Model", "AtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiCalls");
        }
    }
}
