using FactoryPulse.Web.Models;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Client.Subscriptions.Streaming;

namespace FactoryPulse.Web.Services;

public sealed class OpcUaTelemetryService(
    Func<CancellationToken, Task<ManagedSession>> connect,
    OpcUaTelemetryStore telemetryStore,
    IOptions<OpcUaClientOptions> options,
    ILogger<OpcUaTelemetryService> logger) : BackgroundService
{
    internal const string NamespaceUri = "urn:factorypulse:opcua:machine";

    private static readonly IReadOnlyDictionary<OpcUaNodeKey, uint> NodeIdentifiers =
        new Dictionary<OpcUaNodeKey, uint>
        {
            [OpcUaNodeKey.State] = 1001,
            [OpcUaNodeKey.GoodCount] = 1002,
            [OpcUaNodeKey.RejectCount] = 1003,
            [OpcUaNodeKey.TotalCount] = 1004,
            [OpcUaNodeKey.FaultCode] = 1005,
            [OpcUaNodeKey.Heartbeat] = 1006
        };

    private readonly OpcUaClientOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = _options.InitialRetryDelay;
        var hasAttemptedConnection = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            telemetryStore.SetConnectionState(
                hasAttemptedConnection
                    ? OpcUaConnectionState.Reconnecting
                    : OpcUaConnectionState.Connecting);
            hasAttemptedConnection = true;

            try
            {
                await using var session = await connect(stoppingToken);
                var namespaceIndex = session.NamespaceUris.GetIndex(NamespaceUri);
                if (namespaceIndex < 0)
                {
                    throw new ServiceResultException(
                        Opc.Ua.StatusCodes.BadNotFound);
                }

                var nodes = NodeIdentifiers.ToDictionary(
                    pair => pair.Key,
                    pair => new NodeId(pair.Value, (ushort)namespaceIndex));
                await ReadInitialValuesAsync(session, nodes, stoppingToken);
                telemetryStore.SetConnectionState(OpcUaConnectionState.Connected);
                retryDelay = _options.InitialRetryDelay;
                logger.LogInformation(
                    "Connected to OPC UA endpoint {Endpoint} with Basic256Sha256 SignAndEncrypt.",
                    _options.Endpoint);

                await Task.WhenAll(nodes.Select(pair =>
                    MonitorNodeAsync(session, pair.Key, pair.Value, stoppingToken)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                telemetryStore.SetConnectionState(OpcUaConnectionState.Reconnecting);
                logger.LogWarning(
                    exception,
                    "OPC UA connection to {Endpoint} failed. Retrying in {RetryDelay}.",
                    _options.Endpoint,
                    retryDelay);

                await Task.Delay(retryDelay, stoppingToken);
                var nextDelayTicks = Math.Min(
                    retryDelay.Ticks * 2,
                    _options.MaximumRetryDelay.Ticks);
                retryDelay = TimeSpan.FromTicks(nextDelayTicks);
            }
        }

        telemetryStore.SetConnectionState(OpcUaConnectionState.Disconnected);
    }

    private async Task ReadInitialValuesAsync(
        ManagedSession session,
        IReadOnlyDictionary<OpcUaNodeKey, NodeId> nodes,
        CancellationToken cancellationToken)
    {
        (ArrayOf<DataValue> values, ArrayOf<ServiceResult> errors) =
            await session.ReadValuesAsync(nodes.Values.ToArray(), cancellationToken);
        var keys = nodes.Keys.ToArray();

        for (var index = 0; index < keys.Length; index++)
        {
            if (ServiceResult.IsBad(errors[index]))
            {
                throw new ServiceResultException(errors[index]);
            }

            telemetryStore.Apply(keys[index], values[index]);
        }
    }

    private async Task MonitorNodeAsync(
        ManagedSession session,
        OpcUaNodeKey node,
        NodeId nodeId,
        CancellationToken cancellationToken)
    {
        await foreach (var change in session.DefaultStreaming
            .SubscribeDataChangesAsync(nodeId, ct: cancellationToken)
            .WithCancellation(cancellationToken))
        {
            telemetryStore.Apply(node, change.Value);
        }
    }
}
