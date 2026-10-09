using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>trading_mode</c> to <c>trading.decisions</c>, and to what <c>trading.hit_rate</c>
    /// groups by: whether an approved order from the decision could reach the portfolio.
    /// </summary>
    /// <remarks>
    /// Backfilled as <c>Paper</c>, which is true about the history: every decision before stage 7
    /// was made by an engine that executed what the risk gate approved against the simulated
    /// portfolio. The backfill is the <c>ADD COLUMN ... DEFAULT</c> itself rather than an
    /// <c>UPDATE</c>, because this table's own trigger refuses one - and the default is dropped
    /// immediately afterwards, so a row written from now on cannot quietly inherit a mode.
    ///
    /// The view groups by it for the same reason it groups by <c>selection</c>. A Shadow engine
    /// never holds what it decided to buy, so it asks the agents different questions from a Paper
    /// one; a hit rate pooled across the two would be measuring two regimes as one number.
    /// </remarks>
    public partial class DecisionTradingMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Paper, not the generated empty string. The default is what fills the rows already
            // stored, so it has to be the one that is true about them.
            migrationBuilder.AddColumn<string>(
                name: "trading_mode",
                schema: "trading",
                table: "decisions",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "Paper");

            migrationBuilder.Sql("ALTER TABLE trading.decisions ALTER COLUMN trading_mode DROP DEFAULT;");

            migrationBuilder.Sql("DROP VIEW IF EXISTS trading.hit_rate;");
            migrationBuilder.Sql(
                """
                CREATE VIEW trading.hit_rate AS
                SELECT d.team_version,
                       d.trading_mode,
                       d.selection,
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
                GROUP BY 1, 2, 3, 4, 5, 6, 7, 8;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The view reads the column, so it goes first and is rebuilt as DecisionSelection left it.
            migrationBuilder.Sql("DROP VIEW IF EXISTS trading.hit_rate;");

            migrationBuilder.DropColumn(
                name: "trading_mode",
                schema: "trading",
                table: "decisions");

            migrationBuilder.Sql(
                """
                CREATE VIEW trading.hit_rate AS
                SELECT d.team_version,
                       d.selection,
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
                GROUP BY 1, 2, 3, 4, 5, 6, 7;
                """);
        }
    }
}
