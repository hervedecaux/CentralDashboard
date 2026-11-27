using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace DashboardPerso.Data.Migrations
{
    /// <inheritdoc />
    public partial class ActivityPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActivityCategoryId",
                table: "Tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ActivityCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    ParentId = table.Column<int>(type: "INTEGER", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityCategories_ActivityCategories_ParentId",
                        column: x => x.ParentId,
                        principalTable: "ActivityCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WeeklySlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Day = table.Column<int>(type: "INTEGER", nullable: false),
                    Start = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    End = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklySlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeeklySlots_ActivityCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "ActivityCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlannedActivities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Start = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    End = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Done = table.Column<bool>(type: "INTEGER", nullable: false),
                    SlotId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlannedActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlannedActivities_ActivityCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "ActivityCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlannedActivities_WeeklySlots_SlotId",
                        column: x => x.SlotId,
                        principalTable: "WeeklySlots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.InsertData(
                table: "ActivityCategories",
                columns: new[] { "Id", "Color", "Name", "ParentId", "SortOrder" },
                values: new object[,]
                {
                    { 1, "#4f6bed", "Travail", null, 1 },
                    { 2, "#b7702a", "Travaux", null, 2 },
                    { 3, "#2e9e6a", "Tâches ménagères", null, 3 },
                    { 4, "#c2457a", "Loisirs", null, 4 },
                    { 5, "#e0582b", "Sport", 4, 1 },
                    { 6, "#7a5bd1", "Musique", 4, 2 },
                    { 7, "#2b8fb3", "Lecture", 4, 3 },
                    { 8, "#d4a21b", "Photo", 4, 4 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ActivityCategoryId",
                table: "Tasks",
                column: "ActivityCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityCategories_ParentId",
                table: "ActivityCategories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedActivities_CategoryId",
                table: "PlannedActivities",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedActivities_Date",
                table: "PlannedActivities",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedActivities_SlotId",
                table: "PlannedActivities",
                column: "SlotId");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklySlots_CategoryId",
                table: "WeeklySlots",
                column: "CategoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_ActivityCategories_ActivityCategoryId",
                table: "Tasks",
                column: "ActivityCategoryId",
                principalTable: "ActivityCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_ActivityCategories_ActivityCategoryId",
                table: "Tasks");

            migrationBuilder.DropTable(
                name: "PlannedActivities");

            migrationBuilder.DropTable(
                name: "WeeklySlots");

            migrationBuilder.DropTable(
                name: "ActivityCategories");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_ActivityCategoryId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ActivityCategoryId",
                table: "Tasks");
        }
    }
}
