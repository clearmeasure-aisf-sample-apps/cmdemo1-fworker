using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fworker.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "factory");

            migrationBuilder.CreateTable(
                name: "CatalogCopies",
                schema: "factory",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceSha = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FetchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogCopies", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Correlations",
                schema: "factory",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ItemKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Correlations", x => new { x.Kind, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "ItemHistory",
                schema: "factory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ItemKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SessionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenInstances",
                schema: "factory",
                columns: table => new
                {
                    ItemKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenInstances", x => x.ItemKey);
                });

            migrationBuilder.CreateTable(
                name: "ProjectionVersions",
                schema: "factory",
                columns: table => new
                {
                    ItemKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AppliedVersion = table.Column<long>(type: "bigint", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionVersions", x => new { x.ItemKey, x.Target });
                });

            migrationBuilder.CreateTable(
                name: "SessionMailbox",
                schema: "factory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SessionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ItemKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionMailbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Correlations_ItemKey_Generation",
                schema: "factory",
                table: "Correlations",
                columns: new[] { "ItemKey", "Generation" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemHistory_At",
                schema: "factory",
                table: "ItemHistory",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_ItemHistory_ItemKey_Generation_Id",
                schema: "factory",
                table: "ItemHistory",
                columns: new[] { "ItemKey", "Generation", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemHistory_MessageId_Kind",
                schema: "factory",
                table: "ItemHistory",
                columns: new[] { "MessageId", "Kind" },
                unique: true,
                filter: "[MessageId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SessionMailbox_ExpiresAt",
                schema: "factory",
                table: "SessionMailbox",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_SessionMailbox_SessionId_Id",
                schema: "factory",
                table: "SessionMailbox",
                columns: new[] { "SessionId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogCopies",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "Correlations",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "ItemHistory",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "OpenInstances",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "ProjectionVersions",
                schema: "factory");

            migrationBuilder.DropTable(
                name: "SessionMailbox",
                schema: "factory");
        }
    }
}
