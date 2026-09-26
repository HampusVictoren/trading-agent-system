using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Every symbol column in the schema goes from 16 characters to fit Swedish tickers.
    /// </summary>
    /// <remarks>
    /// The one that was already broken is `signal_outcomes.benchmark_symbol`:
    /// `XACT-OMXS30.ST` is 14 characters, so every sweep would have failed with
    /// `22001: value too long for type character varying(10)` - at night, in the one job
    /// nobody watches. The other three would have failed on `ESSITY-B.ST` at 11.
    ///
    /// The view has to be dropped and rebuilt around the alterations. Postgres refuses to
    /// change the type of a column a view selects, and `trading.hit_rate` started selecting
    /// `benchmark_symbol` in the migration immediately before this one - so this dependency is
    /// one this stage created for itself.
    /// </remarks>
    public partial class WidenSymbolColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS trading.hit_rate;");

            migrationBuilder.AlterColumn<string>(
                name: "benchmark_symbol",
                schema: "trading",
                table: "signal_outcomes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.AlterColumn<string>(
                name: "symbol",
                schema: "trading",
                table: "positions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.AlterColumn<string>(
                name: "symbol",
                schema: "trading",
                table: "orders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.AlterColumn<string>(
                name: "symbol",
                schema: "trading",
                table: "decisions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.Sql(
                """
                CREATE VIEW trading.hit_rate AS
                SELECT d.team_version,
                       o.benchmark_symbol,
                       o.horizon_unit,
                       o.horizon_days,
                       d.stance,
                       CASE
                           WHEN d.conviction < 0.4 THEN 'below floor'
                           WHEN d.conviction <= 0.7 THEN 'half'
                           ELSE 'full'
                       END AS conviction_tier,
                       count(*)                                        AS measured,
                       count(*) FILTER (WHERE o.hit)                   AS hits,
                       round(count(*) FILTER (WHERE o.hit)::numeric
                             / count(*), 3)                            AS hit_rate,
                       round(avg(o.excess_return), 6)                  AS avg_excess_gross,
                       round(avg(o.net_edge), 6)                       AS avg_edge_net
                FROM trading.signal_outcomes o
                JOIN trading.decisions d ON d.id = o.decision_id
                WHERE o.status = 'Measured'
                GROUP BY 1, 2, 3, 4, 5, 6;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Narrowing back to 10 will refuse if any stored symbol is longer by then, which
            // is inherent to reversing this rather than a flaw in it: the data would no longer
            // fit the schema it is being returned to.
            migrationBuilder.Sql("DROP VIEW IF EXISTS trading.hit_rate;");

            migrationBuilder.AlterColumn<string>(
                name: "benchmark_symbol",
                schema: "trading",
                table: "signal_outcomes",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<string>(
                name: "symbol",
                schema: "trading",
                table: "positions",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<string>(
                name: "symbol",
                schema: "trading",
                table: "orders",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<string>(
                name: "symbol",
                schema: "trading",
                table: "decisions",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.Sql(
                """
                CREATE VIEW trading.hit_rate AS
                SELECT d.team_version,
                       o.benchmark_symbol,
                       o.horizon_unit,
                       o.horizon_days,
                       d.stance,
                       CASE
                           WHEN d.conviction < 0.4 THEN 'below floor'
                           WHEN d.conviction <= 0.7 THEN 'half'
                           ELSE 'full'
                       END AS conviction_tier,
                       count(*)                                        AS measured,
                       count(*) FILTER (WHERE o.hit)                   AS hits,
                       round(count(*) FILTER (WHERE o.hit)::numeric
                             / count(*), 3)                            AS hit_rate,
                       round(avg(o.excess_return), 6)                  AS avg_excess_gross,
                       round(avg(o.net_edge), 6)                       AS avg_edge_net
                FROM trading.signal_outcomes o
                JOIN trading.decisions d ON d.id = o.decision_id
                WHERE o.status = 'Measured'
                GROUP BY 1, 2, 3, 4, 5, 6;
                """);
        }
    }
}
