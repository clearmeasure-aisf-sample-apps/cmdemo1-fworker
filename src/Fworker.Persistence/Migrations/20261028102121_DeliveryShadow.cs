using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fworker.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryShadow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeliveryShadowComparisons",
                schema: "factory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Deployable = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Release = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    V1Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    V2Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    V1Json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    V2Json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    V1At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    V2At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryShadowComparisons", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryShadowComparisons_At",
                schema: "factory",
                table: "DeliveryShadowComparisons",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryShadowComparisons_Outcome_At",
                schema: "factory",
                table: "DeliveryShadowComparisons",
                columns: new[] { "Outcome", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeliveryShadowComparisons",
                schema: "factory");
        }
    }
}
