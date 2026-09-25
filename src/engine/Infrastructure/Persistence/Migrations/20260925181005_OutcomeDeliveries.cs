using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutcomeDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One row per measurement the agent service has been told about, and no row at
            // all until it lands. A column on signal_outcomes would have been an UPDATE, and
            // that table refuses those - but the separation is also the truer model: whether
            // a measurement has been copied somewhere is a fact about a side effect.
            //
            // Deliberately *not* append-only, unlike the three tables around it. This is a
            // delivery receipt rather than evidence, and deleting one is the supported way
            // to ask for a resend - which is safe because storing is idempotent on the other
            // side. A trigger here would make that an operation you first have to disarm.
            migrationBuilder.CreateTable(
                name: "outcome_deliveries",
                schema: "trading",
                columns: table => new
                {
                    signal_outcome_id = table.Column<long>(type: "bigint", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outcome_deliveries", x => x.signal_outcome_id);
                    table.ForeignKey(
                        name: "fk_outcome_deliveries_signal_outcomes_signal_outcome_id",
                        column: x => x.signal_outcome_id,
                        principalSchema: "trading",
                        principalTable: "signal_outcomes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outcome_deliveries",
                schema: "trading");
        }
    }
}
