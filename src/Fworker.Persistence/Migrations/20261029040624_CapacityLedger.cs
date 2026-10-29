using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fworker.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CapacityLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CapacityLedgers",
                schema: "factory",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    StateJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapacityLedgers", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CapacityLedgers",
                schema: "factory");
        }
    }
}
