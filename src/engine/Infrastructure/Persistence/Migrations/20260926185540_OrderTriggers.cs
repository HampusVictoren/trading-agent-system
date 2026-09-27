using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Why each order was placed. Every row that exists today is a buy the agents argued for,
    /// so `Signal` is the truth about the history rather than a placeholder for it - which is
    /// the difference between this and the empty string the generated migration proposed, a
    /// value the enum cannot produce and nothing downstream could read.
    /// </summary>
    /// <remarks>
    /// The existing rows are filled by the column default rather than by an UPDATE, and that is
    /// not a style choice: `trading.orders` is append-only, enforced by a trigger that raises on
    /// UPDATE, so a backfill statement would fail inside its own migration. ADD COLUMN with a
    /// default is not an UPDATE and never fires it.
    ///
    /// The default is then dropped, because it has done its work. Leaving it would mean an
    /// INSERT that forgot the column silently recorded a stop-loss as something the agents
    /// asked for - a lie about the one column that exists to tell those two apart.
    /// </remarks>
    public partial class OrderTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "triggered_by",
                schema: "trading",
                table: "orders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Signal");

            migrationBuilder.Sql("ALTER TABLE trading.orders ALTER COLUMN triggered_by DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversible in full, unlike the widening two migrations ago: dropping a column
            // loses what it said, but nothing that was true before this ran depended on it.
            migrationBuilder.DropColumn(
                name: "triggered_by",
                schema: "trading",
                table: "orders");
        }
    }
}
