using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fworker.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeliveredTo",
                schema: "factory",
                columns: table => new
                {
                    ItemKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Release = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveredTo", x => new { x.ItemKey, x.Environment });
                });

            migrationBuilder.CreateTable(
                name: "MergedItems",
                schema: "factory",
                columns: table => new
                {
                    ItemKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Commit = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PullRequest = table.Column<int>(type: "int", nullable: false),
                    GatesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DependenciesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    MergedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MergedItems", x => new { x.ItemKey, x.Commit });
                });

            migrationBuilder.CreateIndex(
                name: "IX_MergedItems_Commit",
                schema: "factory",
                table: "MergedItems",
                column: "Commit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeliveredTo",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "MergedItems",
                schema: "factory");
        }
    }
}
