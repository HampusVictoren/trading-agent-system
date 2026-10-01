using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>selection</c> to <c>trading.decisions</c>, and to what <c>trading.hit_rate</c>
    /// groups by: which regime put the instrument in front of the agents.
    /// </summary>
    /// <remarks>
    /// The other half of what the benchmark migration named as still open. A holding is analysed
    /// whatever the ranking says about it, and a candidate is analysed precisely because the
    /// ranking put it near the top - so a hit rate over both is measuring the screen's selection
    /// and the portfolio's inertia as one number, in one cell.
    ///
    /// Backfilled as <c>FixedList</c>, which is true about the history: every decision before this
    /// stage came from a configured ticker list. The backfill is the <c>ADD COLUMN ... DEFAULT</c>
    /// itself rather than an <c>UPDATE</c>, because this table's own trigger refuses one - and the
    /// default is dropped immediately afterwards, so nothing written from now on can quietly
    /// inherit a regime it does not belong to.
    /// </remarks>
    public partial class DecisionSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FixedList, not the generated placeholder. The default is what backfills the 40-odd
            // rows already stored, so it has to be the one that is true about them.
            migrationBuilder.AddColumn<string>(
                name: "selection",
                schema: "trading",
                table: "decisions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "FixedList");

            // Dropped straight away. It existed to fill the history; leaving it would let a row
            // written without a selection claim to have come from a regime that no longer runs.
            migrationBuilder.Sql("ALTER TABLE trading.decisions ALTER COLUMN selection DROP DEFAULT;");

            migrationBuilder.Sql("DROP VIEW IF EXISTS trading.hit_rate;");
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The view reads the column, so it goes first and is rebuilt without it.
            migrationBuilder.Sql("DROP VIEW IF EXISTS trading.hit_rate;");

            migrationBuilder.DropColumn(
                name: "selection",
                schema: "trading",
                table: "decisions");

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
