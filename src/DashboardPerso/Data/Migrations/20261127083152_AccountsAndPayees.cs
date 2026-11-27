using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardPerso.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountsAndPayees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "Expenses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PayeeId",
                table: "Expenses",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Label = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ImportId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Payees",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    DefaultCategoryId = table.Column<int>(type: "INTEGER", nullable: true),
                    DefaultIsTransfer = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payees_BudgetCategories_DefaultCategoryId",
                        column: x => x.DefaultCategoryId,
                        principalTable: "BudgetCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.InsertData(
                table: "Accounts",
                columns: new[] { "Id", "ImportId", "Kind", "Label" },
                values: new object[] { 1, "Compte", 0, "Compte courant" });

            // Reprise des provenances existantes (« Compte », « Carte ••5524 · Porteur ») en comptes.
            const string importId = "CASE WHEN instr(Source, ' · ') > 0 THEN substr(Source, 1, instr(Source, ' · ') - 1) ELSE Source END";
            migrationBuilder.Sql($@"
                INSERT INTO Accounts (Label, Kind, ImportId)
                SELECT MIN(Source), 1, {importId} FROM Expenses
                WHERE Source <> 'Compte'
                GROUP BY {importId};");
            migrationBuilder.Sql($@"
                UPDATE Expenses SET AccountId = (SELECT a.Id FROM Accounts a WHERE a.ImportId = {importId})
                WHERE Source <> 'Compte';");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Expenses");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_AccountId",
                table: "Expenses",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_PayeeId",
                table: "Expenses",
                column: "PayeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_ImportId",
                table: "Accounts",
                column: "ImportId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payees_DefaultCategoryId",
                table: "Payees",
                column: "DefaultCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Payees_Key",
                table: "Payees",
                column: "Key",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Expenses_Accounts_AccountId",
                table: "Expenses",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Expenses_Payees_PayeeId",
                table: "Expenses",
                column: "PayeeId",
                principalTable: "Payees",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Expenses_Accounts_AccountId",
                table: "Expenses");

            migrationBuilder.DropForeignKey(
                name: "FK_Expenses_Payees_PayeeId",
                table: "Expenses");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "Payees");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_AccountId",
                table: "Expenses");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_PayeeId",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "PayeeId",
                table: "Expenses");

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Expenses",
                type: "TEXT",
                maxLength: 80,
                nullable: false,
                defaultValue: "Compte");
        }
    }
}
