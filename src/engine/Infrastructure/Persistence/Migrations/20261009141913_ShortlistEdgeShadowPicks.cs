using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>trading.shortlist_edge</c> counts a shadowed buy as the agents' pick. Decided by Hampus on
    /// 2026-10-09.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A shadowed buy is a decision the agents argued for and the sizer and the risk gate approved.
    /// The only thing missing is the order, and the order is not what this view measures: it
    /// compares how the names the agents picked did against how the whole shortlist did, from
    /// prices. Without this, a Shadow row always reported no buys and no edge, so a Shadow engine
    /// could not answer the one question this view exists to ask.
    /// </para>
    /// <para>
    /// The two modes stay apart through <c>trading_mode</c>, which <see cref="ShortlistEdgeByMode"/>
    /// made a grouping column. A <c>Halted</c> buy is still not counted: the kill switch stopped it,
    /// and in Paper "bought" has always meant bought. Down restores the previous definition word
    /// for word.
    /// </para>
    /// </remarks>
    public partial class ShortlistEdgeShadowPicks : Migration
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

                       -- The agents' own result: the subset they argued to buy and the engine bought, or in
                       -- Shadow would have bought - an approved buy the gate held back is still the agents'
                       -- pick. A stance of Buy, not merely an execution - see the remarks on why a sale is
                       -- not one. Halted is not a pick: the switch stopped it, so the two modes stay apart
                       -- through the mode column rather than through what counts.
                       count(*) FILTER (WHERE d.outcome IN ('Executed', 'Shadowed')
                                          AND d.stance = 'Buy')            AS bought,
                       round(avg(o.excess_return) FILTER (WHERE d.outcome IN ('Executed', 'Shadowed')
                                                            AND d.stance = 'Buy'), 6)
                                                                            AS bought_excess_gross,

                       -- The question the project turns on. Positive means the agents picked better than the
                       -- ranking that handed them the candidates; negative means the LLM is cost, not value.
                       round(avg(o.excess_return) FILTER (WHERE d.outcome IN ('Executed', 'Shadowed')
                                                            AND d.stance = 'Buy')
                             - avg(o.excess_return), 6)                     AS agents_edge_gross,

                       -- What the account actually earned on those buys, after commission and spread. Beside
                       -- the gross figure rather than instead of it: the shortlist average is a paper
                       -- portfolio that paid no costs, so taking them off one side only would flatter it.
                       round(avg(o.net_edge) FILTER (WHERE d.outcome IN ('Executed', 'Shadowed')
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
    }
}
