using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardPerso.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitsIncomeAndBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsIncome",
                table: "Expenses",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "BankBalance",
                table: "Accounts",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "BankBalanceDate",
                table: "Accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "InitialBalance",
                table: "Accounts",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "InitialBalanceDate",
                table: "Accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExpenseSplits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExpenseId = table.Column<int>(type: "INTEGER", nullable: false),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: true),
                    Amount = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseSplits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseSplits_BudgetCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "BudgetCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExpenseSplits_Expenses_ExpenseId",
                        column: x => x.ExpenseId,
                        principalTable: "Expenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "Accounts",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "BankBalance", "BankBalanceDate", "InitialBalance", "InitialBalanceDate" },
                values: new object[] { null, null, null, null });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSplits_CategoryId",
                table: "ExpenseSplits",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSplits_ExpenseId",
                table: "ExpenseSplits",
                column: "ExpenseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpenseSplits");

            migrationBuilder.DropColumn(
                name: "IsIncome",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "BankBalance",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "BankBalanceDate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "InitialBalance",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "InitialBalanceDate",
                table: "Accounts");
        }
    }
}
