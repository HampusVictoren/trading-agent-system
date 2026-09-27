using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// What selling needs stored: when a holding was last added to and on what horizon, and
    /// what a sale realised.
    /// </summary>
    /// <remarks>
    /// The two columns on `positions` are `NOT NULL`, so every existing holding needs a value.
    /// EF's scaffolded defaults are `0001-01-01` and a horizon of 0, which the time-limit exit
    /// would read as "older than any thesis" and sell on the first cycle - arguably the safe
    /// direction, but by accident rather than by decision, and a migration that invents a
    /// purchase date is worse than one that looks the date up.
    ///
    /// So both are backfilled from the ledger, which already knows: the last buy order for that
    /// symbol, and the horizon of the last decision that executed for it. A holding with
    /// neither keeps the sentinel and will be sold by the time limit, which is the right
    /// outcome for a position nothing in the record accounts for.
    /// </remarks>
    public partial class SellingAndHoldingPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_purchased_at",
                schema: "trading",
                table: "positions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<int>(
                name: "thesis_horizon_days",
                schema: "trading",
                table: "positions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "realised_pnl_amount",
                schema: "trading",
                table: "orders",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "realised_pnl_currency",
                schema: "trading",
                table: "orders",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            // Backfilled from the ledger rather than defaulted. `orders` is append-only and
            // every position began as a buy, so the date is there to be read; the horizon is
            // on the decision that produced that buy, since an order does not carry one.
            migrationBuilder.Sql(
                """
                UPDATE trading.positions p
                   SET last_purchased_at = COALESCE((
                           SELECT max(o.placed_at)
                             FROM trading.orders o
                            WHERE o.portfolio_id = p.portfolio_id
                              AND o.symbol = p.symbol
                              AND o.side = 'Buy'
                       ), p.last_purchased_at),
                       thesis_horizon_days = COALESCE((
                           SELECT d.horizon_days
                             FROM trading.decisions d
                            WHERE d.portfolio_id = p.portfolio_id
                              AND d.symbol = p.symbol
                              AND d.outcome = 'Executed'
                              AND d.horizon_days IS NOT NULL
                            ORDER BY d.recorded_at DESC
                            LIMIT 1
                       ), p.thesis_horizon_days);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_purchased_at",
                schema: "trading",
                table: "positions");

            migrationBuilder.DropColumn(
                name: "thesis_horizon_days",
                schema: "trading",
                table: "positions");

            migrationBuilder.DropColumn(
                name: "realised_pnl_amount",
                schema: "trading",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "realised_pnl_currency",
                schema: "trading",
                table: "orders");
        }
    }
}
