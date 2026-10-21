using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryPulse.Web.Data.Migrations;

[DbContext(typeof(FactoryPulseDbContext))]
[Migration("20261021174500_AddAnalyticsEventIndex")]
public sealed class AddAnalyticsEventIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_ProductionEvents_MachineId_EventType_TimestampUtc_Id",
            table: "ProductionEvents",
            columns: new[] { "MachineId", "EventType", "TimestampUtc", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ProductionEvents_MachineId_EventType_TimestampUtc_Id",
            table: "ProductionEvents");
    }
}
