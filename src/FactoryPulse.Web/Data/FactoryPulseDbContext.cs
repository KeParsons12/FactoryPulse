using FactoryPulse.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace FactoryPulse.Web.Data;

public sealed class FactoryPulseDbContext(DbContextOptions<FactoryPulseDbContext> options)
    : DbContext(options)
{
    public DbSet<PersistedProductionEvent> ProductionEvents => Set<PersistedProductionEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var productionEvent = modelBuilder.Entity<PersistedProductionEvent>();

        productionEvent.ToTable("ProductionEvents");
        productionEvent.HasKey(machineEvent => machineEvent.Id);
        productionEvent.Property(machineEvent => machineEvent.MachineId)
            .HasMaxLength(100)
            .IsRequired();
        productionEvent.Property(machineEvent => machineEvent.EventType)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();
        productionEvent.Property(machineEvent => machineEvent.Details)
            .HasMaxLength(500)
            .IsRequired();
        productionEvent.HasIndex(machineEvent => new
        {
            machineEvent.TimestampUtc,
            machineEvent.Id
        });
    }
}

public sealed class PersistedProductionEvent
{
    public long Id { get; set; }

    public string MachineId { get; set; } = string.Empty;

    public DateTime TimestampUtc { get; set; }

    public ProductionEventType EventType { get; set; }

    public string Details { get; set; } = string.Empty;

    public long? FaultDurationTicks { get; set; }

    public static PersistedProductionEvent FromModel(ProductionEvent machineEvent) => new()
    {
        MachineId = machineEvent.MachineId,
        TimestampUtc = machineEvent.Timestamp.UtcDateTime,
        EventType = machineEvent.EventType,
        Details = machineEvent.Details,
        FaultDurationTicks = machineEvent.Duration?.Ticks
    };

    public ProductionEvent ToModel() => new(
        MachineId,
        new DateTimeOffset(DateTime.SpecifyKind(TimestampUtc, DateTimeKind.Utc)),
        EventType,
        Details,
        FaultDurationTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null,
        Id);
}
