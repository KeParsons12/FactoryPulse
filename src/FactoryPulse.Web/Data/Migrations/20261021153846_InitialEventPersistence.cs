using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryPulse.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialEventPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductionEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MachineId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Details = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    FaultDurationTicks = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionEvents_TimestampUtc_Id",
                table: "ProductionEvents",
                columns: new[] { "TimestampUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductionEvents");
        }
    }
}
