using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>trading.shortlist_edge</c>: the buys of a trading day's shortlist against the
    /// shortlist itself, which is the question this project turns on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No new measurement.</b> The planned shape of this work was a second population in
    /// <c>MeasurementWorker</c> - the shortlisted instruments nobody bought. They are already
    /// measured: stage 4 scores <i>every</i> signal at the fixed horizons, including HOLD and
    /// everything the risk gate refused, precisely because measuring only the trades that went
    /// through measures the wrong population. So what was missing was never the data. It was a
    /// join from a measurement back to the shortlist it came from, and that is a view.
    /// </para>
    /// <para>
    /// <b>It compares gross against gross.</b> The shortlist average is a paper portfolio that
    /// paid no commission and no spread, so subtracting costs from the bought side alone would
    /// flatter the screen by about three basis points a round trip. <c>bought_edge_net</c> sits
    /// beside the gross figure rather than replacing it, which is the same reasoning that put
    /// gross and net side by side in <c>hit_rate</c>.
    /// </para>
    /// <para>
    /// <b>And it averages <c>excess_return</c>, not <c>net_edge</c>.</b> That is not a
    /// preference: <c>net_edge</c> is null for every HOLD, because a HOLD has no edge to
    /// compute - only a band it stays inside - and HOLD is the answer the agents give most
    /// often. Averaging it across a shortlist would silently average the buys and sells alone,
    /// which is the comparison this view exists to make, inverted into a number that reads like
    /// data. <c>excess_return</c> is a fact about prices rather than about a stance, and it is
    /// populated on every measured row.
    /// </para>
    /// <para>
    /// The join is on <c>(symbol, trading day)</c>, which is one-to-one because the engine
    /// analyses an instrument at most once a day and the shortlist holds at most one row per
    /// instrument per day. A cycle that straddles midnight UTC can leave a decision stamped
    /// with the day after its shortlist - a window of a few minutes once a day, costing that
    /// one decision its join rather than producing a wrong number.
    /// </para>
    /// <para>
    /// A held instrument that the screen also ranked appears here, because the shortlist row
    /// exists even though the decision was taken as a <c>Holding</c>. That is deliberate: how
    /// the screen rated what the portfolio already owned is worth being able to ask.
    /// </para>
    /// <para>
    /// <b>What it cannot answer</b> is whether the <i>ranking</i> works, because the agent
    /// service truncates to the requested limit and the instruments that ranked below the cut
    /// are stored nowhere. That needs the contract to carry every ranked instrument with a
    /// flag for the ones selected, and it is its own piece of work rather than a line here.
    /// </para>
    /// </remarks>
    public partial class ShortlistEdge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS trading.shortlist_edge;");
            migrationBuilder.Sql(
                """
                CREATE VIEW trading.shortlist_edge AS
                SELECT s.screened_on,
                       d.team_version,
                       o.benchmark_symbol,
                       o.horizon_unit,
                       o.horizon_days,

                       -- The screen's own result: every shortlisted instrument that has been measured,
                       -- whatever the agents then said about it. This is the control.
                       count(*)                                                        AS shortlisted,
                       round(avg(o.excess_return), 6)                                  AS shortlist_excess_gross,

                       -- The agents' result: the subset the engine actually bought.
                       count(*) FILTER (WHERE d.outcome = 'Executed')                  AS bought,
                       round(avg(o.excess_return) FILTER (WHERE d.outcome = 'Executed'), 6)
                                                                                       AS bought_excess_gross,

                       -- The question the project turns on. Positive means the agents picked better than
                       -- the ranking that handed them the candidates; negative means the LLM is cost.
                       round(avg(o.excess_return) FILTER (WHERE d.outcome = 'Executed')
                             - avg(o.excess_return), 6)                                AS agents_edge_gross,

                       -- What the account actually earned on the buys, after commission and spread. Beside
                       -- the gross figure rather than instead of it: the shortlist average is a paper
                       -- portfolio that paid no costs, so taking costs off one side only would flatter it.
                       round(avg(o.net_edge) FILTER (WHERE d.outcome = 'Executed'), 6)  AS bought_edge_net
                FROM trading.shortlists s
                JOIN trading.decisions d
                       ON d.symbol = s.symbol
                      AND (d.requested_at AT TIME ZONE 'UTC')::date = s.screened_on
                JOIN trading.signal_outcomes o
                       ON o.decision_id = d.id
                WHERE s.rank IS NOT NULL
                  AND o.status = 'Measured'
                GROUP BY 1, 2, 3, 4, 5;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS trading.shortlist_edge;");
        }
    }
}
