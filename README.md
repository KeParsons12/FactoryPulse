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
