using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fworker.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkItemSaga : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkItems",
                schema: "factory",
                columns: table => new
                {
                    ItemKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Repository = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Plan = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Step = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StateJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItems", x => x.ItemKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_Repository_Status",
                schema: "factory",
                table: "WorkItems",
                columns: new[] { "Repository", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkItems",
                schema: "factory");
        }
    }
}
