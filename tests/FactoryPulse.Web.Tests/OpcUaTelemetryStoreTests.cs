using FactoryPulse.Web.Models;
using FactoryPulse.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Opc.Ua;

namespace FactoryPulse.Web.Tests;

public sealed class OpcUaTelemetryStoreTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GoodValuesAreConvertedIntoACompleteTelemetrySnapshot()
    {
        var timeProvider = new ManualTimeProvider(InitialTime);
        var store = CreateStore(timeProvider);
        store.SetConnectionState(OpcUaConnectionState.Connected);

        Assert.True(store.Apply(OpcUaNodeKey.State, Value("Running")));
        Assert.True(store.Apply(OpcUaNodeKey.GoodCount, Value(12u)));
        Assert.True(store.Apply(OpcUaNodeKey.RejectCount, Value(2u)));
        Assert.True(store.Apply(OpcUaNodeKey.TotalCount, Value(14u)));
        Assert.True(store.Apply(OpcUaNodeKey.FaultCode, Value(string.Empty)));
        Assert.True(store.Apply(OpcUaNodeKey.Heartbeat, Value(99u)));

        var snapshot = store.GetSnapshot();
        Assert.Equal(OpcUaConnectionState.Connected, snapshot.ConnectionState);
        Assert.Equal("Running", snapshot.MachineState);
        Assert.Equal(12u, snapshot.GoodCount);
        Assert.Equal(2u, snapshot.RejectCount);
        Assert.Equal(14u, snapshot.TotalCount);
        Assert.Equal(string.Empty, snapshot.FaultCode);
        Assert.Equal(99u, snapshot.Heartbeat);
        Assert.Equal(InitialTime, snapshot.LastGoodDataTimestamp);
        Assert.True(snapshot.HasValidData);
        Assert.False(snapshot.IsStale);
        Assert.Null(snapshot.Warning);
    }

    [Fact]
    public void BadQualityAndWrongTypesAreRejectedAndRemainVisibleUntilRecovery()
    {
        var store = CreateStore(new ManualTimeProvider(InitialTime));
        store.SetConnectionState(OpcUaConnectionState.Connected);

        Assert.False(store.Apply(
            OpcUaNodeKey.GoodCount,
            Value(4u, StatusCodes.BadCommunicationError)));
        Assert.Contains("non-good", store.GetSnapshot().Warning);

        Assert.True(store.Apply(OpcUaNodeKey.State, Value("Running")));
        Assert.Contains("GoodCount", store.GetSnapshot().Warning);

        Assert.False(store.Apply(OpcUaNodeKey.GoodCount, Value("4")));
        Assert.Contains("unexpected", store.GetSnapshot().Warning);

        Assert.True(store.Apply(OpcUaNodeKey.GoodCount, Value(4u)));
        Assert.DoesNotContain("GoodCount", store.GetSnapshot().Warning ?? string.Empty);
    }

    [Fact]
    public void ConnectedDataBecomesStaleAndRecoversOnTheNextGoodUpdate()
    {
        var timeProvider = new ManualTimeProvider(InitialTime);
        var store = CreateStore(timeProvider, staleAfter: TimeSpan.FromSeconds(5));
        store.SetConnectionState(OpcUaConnectionState.Connected);
        Assert.True(store.Apply(OpcUaNodeKey.Heartbeat, Value(1u)));
        Assert.Equal(OpcUaConnectionState.Connected, store.GetSnapshot().ConnectionState);

        timeProvider.Advance(TimeSpan.FromSeconds(6));
        var stale = store.GetSnapshot();
        Assert.True(stale.IsStale);
        Assert.Equal(OpcUaConnectionState.Reconnecting, stale.ConnectionState);
        Assert.Contains("stale", stale.Warning);

        Assert.True(store.Apply(OpcUaNodeKey.Heartbeat, Value(2u)));
        var recovered = store.GetSnapshot();
        Assert.False(recovered.IsStale);
        Assert.Equal(OpcUaConnectionState.Connected, recovered.ConnectionState);
        Assert.Equal(2u, recovered.Heartbeat);
    }

    [Fact]
    public void DisconnectAndReconnectStateChangesPreserveButStalePriorData()
    {
        var store = CreateStore(new ManualTimeProvider(InitialTime));
        store.SetConnectionState(OpcUaConnectionState.Connected);
        Assert.True(store.Apply(OpcUaNodeKey.TotalCount, Value(20u)));

        store.SetConnectionState(OpcUaConnectionState.Reconnecting);
        var disconnected = store.GetSnapshot();
        Assert.True(disconnected.IsStale);
        Assert.Equal(20u, disconnected.TotalCount);

        store.SetConnectionState(OpcUaConnectionState.Connected);
        Assert.True(store.Apply(OpcUaNodeKey.TotalCount, Value(21u)));
        var reconnected = store.GetSnapshot();
        Assert.False(reconnected.IsStale);
        Assert.Equal(21u, reconnected.TotalCount);
    }

    [Fact]
    public void ExternalTelemetryDoesNotModifyTheInternalSimulator()
    {
        var timeProvider = new ManualTimeProvider(InitialTime);
        var telemetryStore = CreateStore(timeProvider);
        using var simulator = new MachineSimulator(
            timeProvider,
            new AcceptingEventStore(),
            NullLogger<MachineSimulator>.Instance);
        var before = simulator.GetMachineSnapshot();

        telemetryStore.SetConnectionState(OpcUaConnectionState.Connected);
        telemetryStore.Apply(OpcUaNodeKey.TotalCount, Value(500u));
        telemetryStore.Apply(OpcUaNodeKey.State, Value("Faulted"));

        var after = simulator.GetMachineSnapshot();
        Assert.Equal(before, after);
        Assert.Equal(500u, telemetryStore.GetSnapshot().TotalCount);
    }

    private static OpcUaTelemetryStore CreateStore(
        TimeProvider timeProvider,
        TimeSpan? staleAfter = null) =>
        new(
            timeProvider,
            Options.Create(new OpcUaClientOptions
            {
                Endpoint = "opc.tcp://localhost:62560/FactoryPulse",
                StaleAfter = staleAfter ?? TimeSpan.FromSeconds(5)
            }));

    private static DataValue Value(object value, StatusCode? statusCode = null)
    {
        var variant = value switch
        {
            string stringValue => Variant.From(stringValue),
            uint uintValue => Variant.From(uintValue),
            _ => throw new ArgumentException($"Unsupported test value type {value.GetType().Name}.", nameof(value))
        };

        return new DataValue(variant, statusCode ?? StatusCodes.Good);
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialTime;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class AcceptingEventStore : IProductionEventStore
    {
        public bool TryEnqueue(ProductionEvent machineEvent) => true;

        public PersistenceHealthSnapshot GetHealthSnapshot() => new(
            PersistenceHealthStatus.Healthy, 0, 100, null);

        public Task<IReadOnlyList<ProductionEvent>> GetRecentEventsAsync(
            int count = 20,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProductionEvent>>([]);

        public Task<ProductionEvent?> GetLatestFaultEventAsync(
            string machineId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProductionEvent?>(null);

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
