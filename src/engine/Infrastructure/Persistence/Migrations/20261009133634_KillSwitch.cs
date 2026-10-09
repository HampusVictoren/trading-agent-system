using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds <c>trading.kill_switch</c>: every time trading was stopped or started by hand, and why.
    /// The latest row is the switch's state.
    /// </summary>
    /// <remarks>
    /// Append-only, by the same trigger function as the ledger and the decisions, so the history
    /// of who stopped trading and why cannot be tidied away afterwards. Releasing the switch is a
    /// new row.
    ///
    /// Seeded with one released row, so that the state after a migration is a state somebody
    /// wrote. The engine reads an empty table as engaged - it fails closed - which makes the seed
    /// the difference between a fresh database that trades and one that refuses to until an
    /// operator has said otherwise. Released is right here because <c>Trading:Mode</c> already
    /// decides whether anything is placed, and a fresh checkout ships Shadow.
    /// </remarks>
    public partial class KillSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kill_switch",
                schema: "trading",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    engaged = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    changed_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValueSql: "current_user")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kill_switch", x => x.id);
                    table.CheckConstraint("ck_kill_switch_reason_is_not_blank", "btrim(reason) <> ''");
                });

            migrationBuilder.Sql("""
                CREATE TRIGGER kill_switch_is_append_only
                    BEFORE UPDATE OR DELETE ON trading.kill_switch
                    FOR EACH STATEMENT EXECUTE FUNCTION trading.refuse_rewriting_history();
                """);

            migrationBuilder.Sql("""
                INSERT INTO trading.kill_switch (engaged, reason)
                VALUES (false, 'Created released by the KillSwitch migration. Trading:Mode decides what an approved order does.');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The trigger goes with the table. The function stays: InitialTradingSchema owns it,
            // and the ledger's and the decisions' triggers still use it.
            migrationBuilder.DropTable(
                name: "kill_switch",
                schema: "trading");
        }
    }
}
