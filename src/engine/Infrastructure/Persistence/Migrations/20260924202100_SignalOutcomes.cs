using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SignalOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "signal_outcomes",
                schema: "trading",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    decision_id = table.Column<long>(type: "bigint", nullable: false),
                    horizon_unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    horizon_days = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    benchmark_symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    measured_on = table.Column<DateOnly>(type: "date", nullable: true),
                    measured_price = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    instrument_return = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    benchmark_return = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    excess_return = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    cost_fraction = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    net_edge = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    hit = table.Column<bool>(type: "boolean", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signal_outcomes", x => x.id);
                    table.ForeignKey(
                        name: "fk_signal_outcomes_decisions_decision_id",
                        column: x => x.decision_id,
                        principalSchema: "trading",
                        principalTable: "decisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_signal_outcomes_decision_id_horizon_unit_horizon_days",
                schema: "trading",
                table: "signal_outcomes",
                columns: new[] { "decision_id", "horizon_unit", "horizon_days" },
                unique: true);

            // Append-only, like the two tables it joins. A measurement that can be corrected
            // afterwards is not evidence, and this is the table the whole stage exists to
            // fill. The function was created by the first migration.
            migrationBuilder.Sql("""
                CREATE TRIGGER signal_outcomes_are_append_only
                    BEFORE UPDATE OR DELETE ON trading.signal_outcomes
                    FOR EACH STATEMENT EXECUTE FUNCTION trading.refuse_rewriting_history();
                """);

            // The minimum report, as the roadmap asks for it: hit rate against the index per
            // team version and conviction level. A view rather than a query in code, because
            // this data will be looked at from psql at least as often as from an application
            // - and because a report that needs a deploy to change is a report nobody runs.
            //
            // The conviction boundaries repeat ConvictionTier's constants. That duplication
            // is deliberate, and guarded: a test drives decisions either side of each
            // boundary and fails if the view and the C# ever disagree.
            migrationBuilder.Sql("""
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The view depends on the table, so it goes first - DROP TABLE would refuse
            // otherwise, and a down migration that fails is worse than none.
            migrationBuilder.Sql("DROP VIEW IF EXISTS trading.hit_rate;");
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS signal_outcomes_are_append_only ON trading.signal_outcomes;");

            migrationBuilder.DropTable(
                name: "signal_outcomes",
                schema: "trading");
        }
    }
}
