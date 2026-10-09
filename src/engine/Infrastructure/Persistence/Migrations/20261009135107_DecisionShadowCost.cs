using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>decisions.shadow_cost</c>: what a buy Shadow mode recorded but did not place would have
    /// cost, so a day's shadow buys count against the daily deployment limit the way placed ones
    /// do. Nullable with no backfill: no row before this one was a shadow buy, since Shadow
    /// arrived in the same pull request.
    /// </summary>
    public partial class DecisionShadowCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "shadow_cost",
                schema: "trading",
                table: "decisions",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_decisions_shadow_cost_only_when_shadowed",
                schema: "trading",
                table: "decisions",
                sql: "shadow_cost IS NULL OR (outcome = 'Shadowed' AND shadow_cost > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_decisions_shadow_cost_only_when_shadowed",
                schema: "trading",
                table: "decisions");

            migrationBuilder.DropColumn(
                name: "shadow_cost",
                schema: "trading",
                table: "decisions");
        }
    }
}
