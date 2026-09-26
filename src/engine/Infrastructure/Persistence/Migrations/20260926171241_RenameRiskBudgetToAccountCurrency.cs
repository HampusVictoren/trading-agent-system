using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace engine.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameRiskBudgetToAccountCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "available_risk_budget_usd",
                schema: "trading",
                table: "decisions",
                newName: "available_risk_budget");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "available_risk_budget",
                schema: "trading",
                table: "decisions",
                newName: "available_risk_budget_usd");
        }
    }
}
