# FactoryPulse

FactoryPulse is a small .NET 10 Blazor manufacturing simulator for a single machine. It supports operator-controlled machine states, simulated fault handling, and SQLite-backed event history.

## Local operation

Run the HTTP development profile:

```powershell
dotnet run --project src/FactoryPulse.Web --launch-profile http
```

The SQLite database is created automatically at `%LOCALAPPDATA%\FactoryPulse\factorypulse.db`. Set `FactoryPulse:DatabasePath` to override that location. Production totals reset on restart. An uncleared fault is reconstructed from persisted fault events; otherwise the simulator starts Idle.

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
