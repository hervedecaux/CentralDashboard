using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardPerso.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnnualBudgetAndSubcategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "MonthlyBudget",
                table: "BudgetCategories",
                newName: "AnnualBudget");

            // Les budgets saisis jusqu'ici étaient mensuels : on les convertit en annuels.
            migrationBuilder.Sql("UPDATE BudgetCategories SET AnnualBudget = AnnualBudget * 12;");

            migrationBuilder.AddColumn<int>(
                name: "ParentId",
                table: "BudgetCategories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "BudgetCategories",
                keyColumn: "Id",
                keyValue: 1,
                column: "ParentId",
                value: null);

            migrationBuilder.UpdateData(
                table: "BudgetCategories",
                keyColumn: "Id",
                keyValue: 2,
                column: "ParentId",
                value: null);

            migrationBuilder.UpdateData(
                table: "BudgetCategories",
                keyColumn: "Id",
                keyValue: 3,
                column: "ParentId",
                value: null);

            migrationBuilder.UpdateData(
                table: "BudgetCategories",
                keyColumn: "Id",
                keyValue: 4,
                column: "ParentId",
                value: null);

            migrationBuilder.UpdateData(
                table: "BudgetCategories",
                keyColumn: "Id",
                keyValue: 5,
                column: "ParentId",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetCategories_ParentId",
                table: "BudgetCategories",
                column: "ParentId");

            migrationBuilder.AddForeignKey(
                name: "FK_BudgetCategories_BudgetCategories_ParentId",
                table: "BudgetCategories",
                column: "ParentId",
                principalTable: "BudgetCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BudgetCategories_BudgetCategories_ParentId",
                table: "BudgetCategories");

            migrationBuilder.DropIndex(
                name: "IX_BudgetCategories_ParentId",
                table: "BudgetCategories");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "BudgetCategories");

            migrationBuilder.RenameColumn(
                name: "AnnualBudget",
                table: "BudgetCategories",
                newName: "MonthlyBudget");

            migrationBuilder.Sql("UPDATE BudgetCategories SET MonthlyBudget = ROUND(MonthlyBudget / 12.0, 2);");
        }
    }
}
