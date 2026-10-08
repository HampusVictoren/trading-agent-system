using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>trading.shortlists</c>: what each trading day's screen said about each instrument -
    /// a rank with the figures behind it, or the reason it was left out.
    /// </summary>
    /// <remarks>
    /// One table with either half's columns left null, following <c>decisions</c>, because a log
    /// row has to be able to say what happened to an instrument rather than be a fully populated
    /// candidate. Append-only for the same reason <c>decisions</c> is: this is the record the
    /// stage's own question is answered from - did the agents beat the screen that picked their
    /// candidates - and a shortlist that can be edited afterwards cannot answer it.
    ///
    /// The unique index on (screened_on, symbol) is the load-bearing one. It makes "one verdict
    /// per instrument per trading day" a database rule, and it is what makes the engine's
    /// "have I screened today?" safe against itself: two cycles that both decided to screen
    /// cannot both store a day, and the loser's transaction fails rather than doubling it.
    /// </remarks>
    public partial class Shortlists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shortlists",
                schema: "trading",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    screened_on = table.Column<DateOnly>(type: "date", nullable: false),
                    screened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    symbol = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: true),
                    score = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    return_3m = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    volatility_30d = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    median_dollar_volume = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    rejected_because = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shortlists", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_shortlists_screened_on_rank",
                schema: "trading",
                table: "shortlists",
                columns: new[] { "screened_on", "rank" });

            migrationBuilder.CreateIndex(
                name: "ix_shortlists_screened_on_symbol",
                schema: "trading",
                table: "shortlists",
                columns: new[] { "screened_on", "symbol" },
                unique: true);

            // Append-only, like decisions and the ledger. The function was created by the first
            // migration.
            migrationBuilder.Sql("""
                CREATE TRIGGER shortlists_are_append_only
                    BEFORE UPDATE OR DELETE ON trading.shortlists
                    FOR EACH STATEMENT EXECUTE FUNCTION trading.refuse_rewriting_history();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The trigger goes with the table, but dropping it first says so out loud rather
            // than leaving a reader to know that DROP TABLE takes its triggers along.
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS shortlists_are_append_only ON trading.shortlists;");

            migrationBuilder.DropTable(
                name: "shortlists",
                schema: "trading");
        }
    }
}
