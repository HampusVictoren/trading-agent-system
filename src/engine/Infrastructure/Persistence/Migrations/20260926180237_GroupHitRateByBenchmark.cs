using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>benchmark_symbol</c> to what <c>trading.hit_rate</c> groups by.
    /// </summary>
    /// <remarks>
    /// A latent defect, closed by the change that would eventually have triggered it. The row
    /// has always recorded which benchmark it was scored against; the view dropped that from
    /// its grouping, so two decisions alike in team version, horizon, stance and conviction
    /// tier but scored against different markets would land in one cell. A hit rate against
    /// two markets is two numbers, and one cell holding both is noise that reads as data.
    ///
    /// The unique index does not prevent it: it is on (decision_id, horizon_unit,
    /// horizon_days), so one decision cannot have two benchmarks at one horizon, but two
    /// decisions in the same cell certainly can.
    ///
    /// Today's history happens not to collide - the 36 stored USD decisions carry an older
    /// team version and only their one-trading-day horizon has been measured, so the rows the
    /// next sweep writes against XACT-OMXS30.ST land in cells of their own. That is a
    /// coincidence of this particular history rather than a guarantee, and moving the
    /// benchmark without moving the team is exactly the shape of change that breaks it - which
    /// is what this pull request does.
    ///
    /// It also makes the mismatch visible rather than merely separate: a cell scoring American
    /// shares against a Swedish index now says so in a column instead of having to be inferred
    /// from the symbols. The other half - that nothing records which regime a decision belongs
    /// to - is a column on `decisions` that this stage's last pull request adds.
    /// </remarks>
    public partial class GroupHitRateByBenchmark : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS trading.hit_rate;");
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
            migrationBuilder.Sql("DROP VIEW IF EXISTS trading.hit_rate;");
            migrationBuilder.Sql(
                """
                CREATE VIEW trading.hit_rate AS
                SELECT d.team_version,
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
                GROUP BY 1, 2, 3, 4, 5;
                """);
        }
    }
}
