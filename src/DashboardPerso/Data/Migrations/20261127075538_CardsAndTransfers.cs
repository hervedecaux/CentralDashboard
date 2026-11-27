using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardPerso.Data.Migrations
{
    /// <inheritdoc />
    public partial class CardsAndTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTransfer",
                table: "Expenses",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Expenses",
                type: "TEXT",
                maxLength: 80,
                nullable: false,
                defaultValue: "Compte");

            migrationBuilder.AlterColumn<int>(
                name: "CategoryId",
                table: "CategoryRules",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<bool>(
                name: "MarkAsTransfer",
                table: "CategoryRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsTransfer",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "MarkAsTransfer",
                table: "CategoryRules");

            migrationBuilder.AlterColumn<int>(
                name: "CategoryId",
                table: "CategoryRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
