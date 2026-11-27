using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardPerso.Data.Migrations
{
    /// <inheritdoc />
    public partial class IncomeCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "BudgetCategories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
            // Les catégories existantes restent des catégories de dépenses (Kind = 0).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "BudgetCategories");
        }
    }
}
