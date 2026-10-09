using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>trading.shortlist_edge</c> grouped by <c>decisions.trading_mode</c> as well, the way
    /// <c>hit_rate</c> already is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Shadow engine and a Paper one ask different questions, because a Shadow engine never holds
    /// what it decided to buy. Pooling their shortlists would average two populations into one
    /// number. The mode is a grouping column rather than a filter, so both stay readable.
    /// </para>
    /// <para>
    /// <c>bought</c> still means an executed buy, so a Shadow row reports none and no agents'
    /// edge. Whether a shadowed buy should count as the agents' pick is an open decision, not
    /// something to settle inside a view. Everything else about the view is as
    /// <see cref="ShortlistEdge"/> describes it.
    /// </para>
    /// <para>
    /// Dropped and recreated rather than replaced, because <c>CREATE OR REPLACE VIEW</c> can only
    /// add columns at the end and the mode belongs beside the day. Down restores the previous
    /// definition word for word.
    /// </para>
    /// </remarks>
    public partial class ShortlistEdgeByMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS trading.shortlist_edge;");
            migrationBuilder.Sql(
                """
                CREATE VIEW trading.shortlist_edge AS
                SELECT s.screened_on,
                       d.trading_mode,
                       d.team_version,
                       o.benchmark_symbol,
                       o.horizon_unit,
                       o.horizon_days,

                       -- The control: every shortlisted instrument that has been measured, whatever the
                       -- agents then said about it. A HOLD, a refused sale and a rejected order all belong
                       -- here - the question is how the names the screen put forward did.
                       count(*)                                            AS shortlisted,
                       round(avg(o.excess_return), 6)                       AS shortlist_excess_gross,

                       -- The agents' own result: the subset they argued to buy and the engine bought. A
                       -- stance of Buy, not merely an execution - see the remarks on why a sale is not one.
                       -- In Shadow nothing executes, so a Shadow row reports no buys; its control is
                       -- still a measured shortlist, kept apart from Paper's by the mode column.
                       count(*) FILTER (WHERE d.outcome = 'Executed'
                                          AND d.stance = 'Buy')            AS bought,
                       round(avg(o.excess_return) FILTER (WHERE d.outcome = 'Executed'
                                                            AND d.stance = 'Buy'), 6)
                                                                            AS bought_excess_gross,

                       -- The question the project turns on. Positive means the agents picked better than the
                       -- ranking that handed them the candidates; negative means the LLM is cost, not value.
                       round(avg(o.excess_return) FILTER (WHERE d.outcome = 'Executed'
                                                            AND d.stance = 'Buy')
                             - avg(o.excess_return), 6)                     AS agents_edge_gross,

                       -- What the account actually earned on those buys, after commission and spread. Beside
                       -- the gross figure rather than instead of it: the shortlist average is a paper
                       -- portfolio that paid no costs, so taking them off one side only would flatter it.
                       round(avg(o.net_edge) FILTER (WHERE d.outcome = 'Executed'
                                                       AND d.stance = 'Buy'), 6)
                                                                            AS bought_edge_net
                FROM trading.shortlists s
                JOIN trading.decisions d
                       ON d.symbol = s.symbol
                      AND (d.requested_at AT TIME ZONE 'UTC')::date = s.screened_on
                JOIN trading.signal_outcomes o
                       ON o.decision_id = d.id
                WHERE s.rank IS NOT NULL
                  AND o.status = 'Measured'
                GROUP BY 1, 2, 3, 4, 5, 6;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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

                       -- The control: every shortlisted instrument that has been measured, whatever the
                       -- agents then said about it. A HOLD, a refused sale and a rejected order all belong
                       -- here - the question is how the names the screen put forward did.
                       count(*)                                            AS shortlisted,
                       round(avg(o.excess_return), 6)                       AS shortlist_excess_gross,

                       -- The agents' own result: the subset they argued to buy and the engine bought. A
                       -- stance of Buy, not merely an execution - see the remarks on why a sale is not one.
                       count(*) FILTER (WHERE d.outcome = 'Executed'
                                          AND d.stance = 'Buy')            AS bought,
                       round(avg(o.excess_return) FILTER (WHERE d.outcome = 'Executed'
                                                            AND d.stance = 'Buy'), 6)
                                                                            AS bought_excess_gross,

                       -- The question the project turns on. Positive means the agents picked better than the
                       -- ranking that handed them the candidates; negative means the LLM is cost, not value.
                       round(avg(o.excess_return) FILTER (WHERE d.outcome = 'Executed'
                                                            AND d.stance = 'Buy')
                             - avg(o.excess_return), 6)                     AS agents_edge_gross,

                       -- What the account actually earned on those buys, after commission and spread. Beside
                       -- the gross figure rather than instead of it: the shortlist average is a paper
                       -- portfolio that paid no costs, so taking them off one side only would flatter it.
                       round(avg(o.net_edge) FILTER (WHERE d.outcome = 'Executed'
                                                       AND d.stance = 'Buy'), 6)
                                                                            AS bought_edge_net
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
    }
}
