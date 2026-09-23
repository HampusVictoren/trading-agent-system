using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialTradingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "trading");

            migrationBuilder.CreateTable(
                name: "portfolios",
                schema: "trading",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    cash_balance_amount = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    cash_balance_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_portfolios", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "trading",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    portfolio_id = table.Column<Guid>(type: "uuid", nullable: false),
                    symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    placed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    price_amount = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    price_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.ForeignKey(
                        name: "fk_orders_portfolios_portfolio_id",
                        column: x => x.portfolio_id,
                        principalSchema: "trading",
                        principalTable: "portfolios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "positions",
                schema: "trading",
                columns: table => new
                {
                    symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    portfolio_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    average_purchase_price_amount = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    average_purchase_price_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_positions", x => new { x.portfolio_id, x.symbol });
                    table.ForeignKey(
                        name: "fk_positions_portfolios_portfolio_id",
                        column: x => x.portfolio_id,
                        principalSchema: "trading",
                        principalTable: "portfolios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "decisions",
                schema: "trading",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    portfolio_id = table.Column<Guid>(type: "uuid", nullable: false),
                    symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    team_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    available_risk_budget_usd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    max_position_pct = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    existing_quantity = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    existing_average_price = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    team_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    revisions = table.Column<int>(type: "integer", nullable: true),
                    stance = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    conviction = table.Column<double>(type: "double precision", nullable: true),
                    thesis = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    key_risks = table.Column<string[]>(type: "varchar(300)[]", nullable: false),
                    horizon_days = table.Column<int>(type: "integer", nullable: true),
                    reference_price = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    reference_currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    quote_as_of = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    outcome_reason = table.Column<string>(type: "text", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_decisions_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "trading",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_decisions_portfolios_portfolio_id",
                        column: x => x.portfolio_id,
                        principalSchema: "trading",
                        principalTable: "portfolios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_decisions_correlation_id",
                schema: "trading",
                table: "decisions",
                column: "correlation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_decisions_order_id",
                schema: "trading",
                table: "decisions",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_decisions_portfolio_id",
                schema: "trading",
                table: "decisions",
                column: "portfolio_id");

            migrationBuilder.CreateIndex(
                name: "ix_decisions_symbol_requested_at",
                schema: "trading",
                table: "decisions",
                columns: new[] { "symbol", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_decisions_team_version_outcome",
                schema: "trading",
                table: "decisions",
                columns: new[] { "team_version", "outcome" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_portfolio_id",
                schema: "trading",
                table: "orders",
                column: "portfolio_id");

            // Append-only, said where it cannot be talked out of. The ledger and the decision
            // history are the evidence this whole stage exists to collect, and evidence that
            // can be edited afterwards is not evidence. Nothing in the engine updates either
            // table, so this costs nothing until the day somebody writes the code that does.
            // A statement-level trigger fires even when the statement matches no rows, so
            // "DELETE FROM trading.orders" is refused rather than quietly succeeding.
            //
            // It stops application code, not the schema's owner: engine_svc owns these tables
            // and can drop the trigger. That is the right boundary - a migration is allowed to
            // change the rules, a cycle is not.
            migrationBuilder.Sql("""
                CREATE FUNCTION trading.refuse_rewriting_history() RETURNS trigger
                    LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'trading.% is append-only; % is not allowed',
                        TG_TABLE_NAME, TG_OP;
                END;
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER orders_are_append_only
                    BEFORE UPDATE OR DELETE ON trading.orders
                    FOR EACH STATEMENT EXECUTE FUNCTION trading.refuse_rewriting_history();
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER decisions_are_append_only
                    BEFORE UPDATE OR DELETE ON trading.decisions
                    FOR EACH STATEMENT EXECUTE FUNCTION trading.refuse_rewriting_history();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the tables takes their triggers with them, but the function is owned by
            // the schema rather than by a table. Leaving it behind would make the next Up fail
            // on "function already exists", which is the failure mode a down migration exists
            // to prevent.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS decisions_are_append_only ON trading.decisions;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS orders_are_append_only ON trading.orders;");

            migrationBuilder.DropTable(
                name: "decisions",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "positions",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "trading");

            migrationBuilder.DropTable(
                name: "portfolios",
                schema: "trading");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS trading.refuse_rewriting_history();");
        }
    }
}
