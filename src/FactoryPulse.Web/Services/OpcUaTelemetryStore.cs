using FactoryPulse.Web.Models;
using Microsoft.Extensions.Options;
using Opc.Ua;

namespace FactoryPulse.Web.Services;

public sealed class OpcUaTelemetryStore(
    TimeProvider timeProvider,
    IOptions<OpcUaClientOptions> options) : IOpcUaTelemetry
{
    private readonly Lock _lock = new();
    private readonly HashSet<OpcUaNodeKey> _validNodes = [];
    private readonly Dictionary<OpcUaNodeKey, string> _invalidNodes = [];
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly OpcUaClientOptions _options = options.Value;
    private OpcUaConnectionState _connectionState = OpcUaConnectionState.Disconnected;
    private string? _machineState;
    private uint? _goodCount;
    private uint? _rejectCount;
    private uint? _totalCount;
    private string? _faultCode;
    private uint? _heartbeat;
    private DateTimeOffset? _lastGoodDataTimestamp;
    private DateTimeOffset? _lastReceivedAtUtc;

    public OpcUaTelemetrySnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            var staleByAge = _lastReceivedAtUtc is null ||
                now - _lastReceivedAtUtc > _options.StaleAfter;
            var effectiveConnectionState =
                _connectionState == OpcUaConnectionState.Connected && staleByAge
                    ? OpcUaConnectionState.Reconnecting
                    : _connectionState;
            var isStale = effectiveConnectionState != OpcUaConnectionState.Connected;
            var hasValidData = _validNodes.Count == Enum.GetValues<OpcUaNodeKey>().Length;
            var warning = _invalidNodes.Values.FirstOrDefault();

            if (warning is null && isStale)
            {
                warning = _lastReceivedAtUtc is null
                    ? "No valid OPC UA telemetry has been received."
                    : effectiveConnectionState == OpcUaConnectionState.Reconnecting
                        ? $"OPC UA telemetry is stale; no update was received within {_options.StaleAfter.TotalSeconds:N0} seconds."
                        : "The OPC UA connection is not live. Previously received values are stale.";
            }
            else if (warning is null && !hasValidData)
            {
                warning = "Waiting for a valid value from every configured OPC UA node.";
            }

            return new OpcUaTelemetrySnapshot(
                effectiveConnectionState,
                _options.Endpoint,
                _machineState,
                _goodCount,
                _rejectCount,
                _totalCount,
                _faultCode,
                _heartbeat,
                _lastGoodDataTimestamp,
                isStale,
                hasValidData,
                warning);
        }
    }

    internal void SetConnectionState(OpcUaConnectionState state)
    {
        lock (_lock)
        {
            _connectionState = state;
        }
    }

    internal bool Apply(OpcUaNodeKey node, DataValue value)
    {
        lock (_lock)
        {
            if (!StatusCode.IsGood(value.StatusCode))
            {
                _validNodes.Remove(node);
                _invalidNodes[node] =
                    $"{node} reported non-good OPC UA quality {value.StatusCode}.";
                return false;
            }

            var converted = node switch
            {
                OpcUaNodeKey.State => TrySetString(value, result => _machineState = result),
                OpcUaNodeKey.GoodCount => TrySetUInt32(value, result => _goodCount = result),
                OpcUaNodeKey.RejectCount => TrySetUInt32(value, result => _rejectCount = result),
                OpcUaNodeKey.TotalCount => TrySetUInt32(value, result => _totalCount = result),
                OpcUaNodeKey.FaultCode => TrySetString(value, result => _faultCode = result),
                OpcUaNodeKey.Heartbeat => TrySetUInt32(value, result => _heartbeat = result),
                _ => false
            };

            if (!converted)
            {
                _validNodes.Remove(node);
                _invalidNodes[node] =
                    $"{node} returned an unexpected OPC UA data type.";
                return false;
            }

            _validNodes.Add(node);
            _invalidNodes.Remove(node);
            _lastReceivedAtUtc = _timeProvider.GetUtcNow();
            _lastGoodDataTimestamp = GetTimestamp(value, _lastReceivedAtUtc.Value);
            return true;
        }
    }

    private static bool TrySetString(DataValue value, Action<string> setter)
    {
        if (!value.WrappedValue.TryGetValue(out string? result) || result is null)
        {
            return false;
        }

        setter(result);
        return true;
    }

    private static bool TrySetUInt32(DataValue value, Action<uint> setter)
    {
        if (!value.WrappedValue.TryGetValue(out uint result))
        {
            return false;
        }

        setter(result);
        return true;
    }

    private static DateTimeOffset GetTimestamp(
        DataValue value,
        DateTimeOffset fallback)
    {
        var timestamp = !value.SourceTimestamp.IsNull
            ? value.SourceTimestamp
            : value.ServerTimestamp;
        return timestamp.IsNull
            ? fallback
            : timestamp.ToDateTimeOffset();
    }
}
