namespace FactoryPulse.OpcUa.Simulator;

public sealed class SimulatedMachine
{
    public static SimulatedMachine Shared { get; } = new();

    private readonly Lock _lock = new();
    private string _state = "Running";
    private uint _goodCount;
    private uint _rejectCount;
    private uint _totalCount;
    private string _faultCode = string.Empty;
    private uint _heartbeat;

    private SimulatedMachine()
    {
    }

    public SimulatedMachineSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new SimulatedMachineSnapshot(
                _state,
                _goodCount,
                _rejectCount,
                _totalCount,
                _faultCode,
                _heartbeat);
        }
    }

    public void Advance()
    {
        lock (_lock)
        {
            _heartbeat++;
            if (_state != "Running")
            {
                return;
            }

            _totalCount++;
            if (_totalCount % 10 == 0)
            {
                _rejectCount++;
            }
            else
            {
                _goodCount++;
            }
        }
    }

    public bool InjectFault()
    {
        lock (_lock)
        {
            if (_state == "Faulted")
            {
                return false;
            }

            _state = "Faulted";
            _faultCode = "SIM-OPC-101";
            return true;
        }
    }

    public bool ClearFault()
    {
        lock (_lock)
        {
            if (_state != "Faulted")
            {
                return false;
            }

            _state = "Running";
            _faultCode = string.Empty;
            return true;
        }
    }
}

public sealed record SimulatedMachineSnapshot(
    string State,
    uint GoodCount,
    uint RejectCount,
    uint TotalCount,
    string FaultCode,
    uint Heartbeat);
