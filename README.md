# FactoryPulse

FactoryPulse is a small .NET 10 Blazor manufacturing simulator for a single machine. It supports operator-controlled machine states, simulated fault handling, and SQLite-backed event history.

## Local operation

From the repository root, start the complete demonstration with one command:

```powershell
.\start-demo.ps1
```

The script builds the solution, starts the OPC UA simulator, waits for its endpoint, starts the web application with the HTTP development profile, and waits for the dashboard to respond. Open [http://localhost:5153](http://localhost:5153).

Use the same terminal for the external simulator controls:

- `F` injects fault `SIM-OPC-101`.
- `C` clears the external fault.
- `Q` gracefully stops both applications.
- `Ctrl+C` also stops both applications.

The launcher prefixes child output with `[OPC]` or `[WEB]`. If either application exits unexpectedly, it reports the failure and stops the remaining application. Graceful web shutdown follows the normal hosted-service path so accepted SQLite events can be flushed. A child that does not stop within 15 seconds is terminated by its specific owned process tree; unrelated `dotnet` processes are never targeted.

The SQLite database is created automatically at `%LOCALAPPDATA%\FactoryPulse\factorypulse.db`. Set `FactoryPulse:DatabasePath` to override that location. Production totals reset on restart. An uncleared fault is reconstructed from persisted fault events; otherwise the internal simulator starts Idle.

### Start applications individually

For debugging, start the two applications in separate terminals. Run the OPC UA simulator from the repository root so it uses the same project-local certificate location as the launcher:

```powershell
dotnet run --project src/FactoryPulse.OpcUa.Simulator
```

Run the web application with its HTTP development profile:

```powershell
dotnet run --project src/FactoryPulse.Web --launch-profile http
```

## External OPC UA telemetry

Milestone 5 adds an independent console-based OPC UA machine simulator and a read-only client in the Blazor application. The external values are displayed in the **External OPC UA Telemetry** dashboard section. They do not change the original in-process simulator, its controls, persisted events, counters, or analytics.

The implementation pins the official OPC Foundation .NET packages at version `2.0.1`. The client requires `Basic256Sha256` with `SignAndEncrypt`; an insecure fallback is not configured. Certificates use project-local PKI folders and are never automatically trusted.

The simulator listens only on `opc.tcp://localhost:62560/FactoryPulse`. When running it individually, enter `F`, `C`, or `Q` followed by Enter. Its production counters and fault state are intentionally session-based.

### First-run certificate trust

The first connection is expected to fail because neither application trusts the other's self-signed application certificate. This is the safe default. Run `.\start-demo.ps1` once, stop it with `Q`, and inspect these rejected-certificate folders:

```powershell
Get-ChildItem .\pki\rejected\certs
Get-ChildItem .\src\FactoryPulse.Web\App_Data\OpcUa\pki\rejected\certs
```

Verify the certificate subjects and thumbprints shown in the application logs. Trust only the two certificates created by this FactoryPulse run:

- Move the rejected **FactoryPulse OPC UA Client** public `.der` file from `pki\rejected\certs` to `pki\trusted\certs`.
- Move the rejected **FactoryPulse OPC UA Simulator** public `.der` file from `src\FactoryPulse.Web\App_Data\OpcUa\pki\rejected\certs` to `src\FactoryPulse.Web\App_Data\OpcUa\pki\trusted\certs`.

For example, after replacing each placeholder with the exact verified filename:

```powershell
Move-Item -LiteralPath ".\pki\rejected\certs\<client-thumbprint>.der" -Destination ".\pki\trusted\certs\"
Move-Item -LiteralPath ".\src\FactoryPulse.Web\App_Data\OpcUa\pki\rejected\certs\<server-thumbprint>.der" -Destination ".\src\FactoryPulse.Web\App_Data\OpcUa\pki\trusted\certs\"
```

Restart both applications. Do not move private-key files, trust unrelated rejected certificates, or install these development certificates in a Windows system trust store. All PKI folders are excluded from Git.

### OPC UA address space

The stable namespace URI is `urn:factorypulse:opcua:machine`. The server exposes a read-only `Machine01` object (`ns=<runtime index>;i=1000`) with these numeric identifiers:

| Node | Identifier | OPC UA type |
| --- | ---: | --- |
| `Machine01.State` | `i=1001` | `String` |
| `Machine01.GoodCount` | `i=1002` | `UInt32` |
| `Machine01.RejectCount` | `i=1003` | `UInt32` |
| `Machine01.TotalCount` | `i=1004` | `UInt32` |
| `Machine01.FaultCode` | `i=1005` | `String` |
| `Machine01.Heartbeat` | `i=1006` | `UInt32` |

The namespace index is resolved at runtime rather than assumed. The client validates both OPC UA status quality and exact value type before updating its snapshot. It retains last-known values but marks them stale after five seconds without good data. Connection retry delays are bounded between one and fifteen seconds, while the managed session performs up to five reconnect attempts before the outer connection loop retries. These settings and the endpoint are configurable under `FactoryPulse:OpcUa` in `appsettings.json`.

Current limitations: the OPC UA feed is read-only and local-only; it is not persisted or included in analytics. It represents a separate demo machine process, not the internal FactoryPulse simulator or physical equipment.

## Persistence reliability

The simulator sends events to a bounded in-memory queue before the SQLite writer stores them. These states are deliberately different:

- **Queued** means the event was accepted into process memory but is not yet guaranteed to survive a crash or power loss.
- **Durably persisted** means SQLite successfully committed the event. Only these events appear in the dashboard history.

Operator commands wait for the queue to flush before the dashboard reports that the command was persisted. Periodic unit-completed events are written asynchronously so SQLite work never runs inside the machine state lock.

The queue is limited to 100 accepted events by default. If it fills, the triggering simulation action is rejected without changing counters or machine state. New activity remains disabled until the queue drains. A failed database write is retried three times by default; new activity is paused during retries. If all attempts fail, persistence becomes Unavailable and production and operator state changes are interlocked. Correct the database problem and restart the application to recover.

During graceful shutdown, FactoryPulse drains the accepted queue. An abrupt process termination or power loss can lose events that were queued but had not yet been committed. Already committed SQLite events remain available after restart.

The queue capacity, retry count, and retry delay are configured under `FactoryPulse:EventPersistence` in `appsettings.json`.

## Analytics methodology

The dashboard calculates analytics directly from durably persisted SQLite events for the selected last hour, 24 hours, or 7 days. Time windows are half-open (`start <= timestamp < end`) and use UTC.

- **Total production** is an independent exact-range count of persisted `UnitCompleted` events in the window. Chart buckets use the same half-open UTC boundaries, run in the same database snapshot, and must sum to that total.
- **Production rate** is total production divided by the selected wall-clock hours.
- **Fault count** is the number of `FaultRaised` events in the window.
- **Total fault downtime** is the portion of every fault interval that overlaps the window. A fault raised before the window contributes only its in-window duration; an uncleared fault ends at the report time.
- **Fault-free monitoring percentage** is `(selected monitored time - fault downtime) / selected monitored time`. It is reported only when monitoring coverage spans the entire selected window. This is not conventional manufacturing availability because FactoryPulse does not yet record planned-production time or Running time.
- **MTTR** is the average final duration of faults cleared in the window. It is N/A when none were cleared.
- **Mean fault-free monitored time per fault** is confirmed monitored time without an active fault divided by faults raised in the window. It is N/A without complete monitoring coverage or when no faults occurred. This is not conventional MTBF because Idle and Running time cannot yet be reconstructed reliably from the persisted events.

FactoryPulse persists a unique monitoring-session start and a graceful-stop boundary. A session that has no stop boundary is trusted through the current process only. Time before the first boundary, between sessions, or after an interrupted prior session is unknown and is never treated as confirmed monitored time. Historical production and fault totals remain available for those periods, while fault-free monitoring metrics display N/A with the reason. Monitoring-boundary events are internal analytics records and are intentionally omitted from the recent operator event table.
