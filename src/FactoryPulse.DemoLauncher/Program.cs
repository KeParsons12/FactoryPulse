using System.Diagnostics;
using System.Net.Sockets;

const string dashboardUrl = "http://localhost:5153";
const int opcUaPort = 62560;
const int webPort = 5153;
var repositoryRoot = ResolveRepositoryRoot(args);
var simulatorProject = Path.Combine(
    repositoryRoot,
    "src",
    "FactoryPulse.OpcUa.Simulator",
    "FactoryPulse.OpcUa.Simulator.csproj");
var webProject = Path.Combine(
    repositoryRoot,
    "src",
    "FactoryPulse.Web",
    "FactoryPulse.Web.csproj");

using var processJob = WindowsJobObject.CreateKillOnClose();
using var shutdown = new CancellationTokenSource();
var shutdownReason = "Demo stopped.";
var requestedShutdown = false;
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    requestedShutdown = true;
    shutdownReason = "Ctrl+C received.";
    shutdown.Cancel();
};

ChildProcess? simulator = null;
ChildProcess? web = null;
var exitCode = 0;

try
{
    Console.WriteLine("FactoryPulse demo launcher");
    Console.WriteLine($"Repository: {repositoryRoot}");

    await EnsurePortAvailableAsync(opcUaPort, "OPC UA", shutdown.Token);
    await EnsurePortAvailableAsync(webPort, "dashboard", shutdown.Token);

    Console.WriteLine("[LAUNCHER] Starting OPC UA simulator...");
    simulator = StartChild(
        "OPC",
        repositoryRoot,
        simulatorProject,
        launchProfile: null,
        processJob);
    await WaitForTcpAsync(
        "localhost",
        opcUaPort,
        simulator,
        TimeSpan.FromSeconds(30),
        shutdown.Token);
    Console.WriteLine("[LAUNCHER] OPC UA endpoint is accepting connections.");

    Console.WriteLine("[LAUNCHER] Starting FactoryPulse web application...");
    web = StartChild(
        "WEB",
        Path.GetDirectoryName(webProject)!,
        webProject,
        launchProfile: "http",
        processJob);
    await WaitForHttpAsync(
        new Uri(dashboardUrl),
        web,
        TimeSpan.FromSeconds(45),
        shutdown.Token);

    Console.WriteLine();
    Console.WriteLine($"Dashboard: {dashboardUrl}");
    Console.WriteLine("Controls: F = inject OPC UA fault, C = clear fault, Q = stop demo");
    Console.WriteLine("Ctrl+C also stops both applications.");
    Console.WriteLine("If external telemetry is reconnecting on first run, complete the manual certificate trust steps in README.md.");
    Console.WriteLine();

    while (!shutdown.IsCancellationRequested)
    {
        if (simulator.ExitTask.IsCompleted)
        {
            shutdownReason = $"OPC UA simulator exited unexpectedly with code {await simulator.ExitTask}.";
            exitCode = 1;
            shutdown.Cancel();
            break;
        }

        if (web.ExitTask.IsCompleted)
        {
            shutdownReason = $"Web application exited unexpectedly with code {await web.ExitTask}.";
            exitCode = 1;
            shutdown.Cancel();
            break;
        }

        if (!Console.IsInputRedirected && Console.KeyAvailable)
        {
            var key = char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar);
            switch (key)
            {
                case 'F':
                    await SendCommandAsync(simulator, "F");
                    Console.WriteLine("[LAUNCHER] Inject-fault command sent.");
                    break;
                case 'C':
                    await SendCommandAsync(simulator, "C");
                    Console.WriteLine("[LAUNCHER] Clear-fault command sent.");
                    break;
                case 'Q':
                    requestedShutdown = true;
                    shutdownReason = "Q received.";
                    shutdown.Cancel();
                    break;
            }
        }

        await Task.Delay(100);
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    // Normal Ctrl+C or Q path.
}
catch (Exception exception)
{
    shutdownReason = exception.Message;
    exitCode = 1;
}
finally
{
    Console.WriteLine($"[LAUNCHER] {shutdownReason} Stopping FactoryPulse...");
    await Task.WhenAll(
        StopChildAsync(web, TimeSpan.FromSeconds(15)),
        StopChildAsync(simulator, TimeSpan.FromSeconds(15)));
    Console.WriteLine("[LAUNCHER] Both applications have stopped.");
}

return requestedShutdown ? 0 : exitCode;

static ChildProcess StartChild(
    string label,
    string workingDirectory,
    string projectPath,
    string? launchProfile,
    WindowsJobObject processJob)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add("run");
    startInfo.ArgumentList.Add("--no-build");
    startInfo.ArgumentList.Add("--project");
    startInfo.ArgumentList.Add(projectPath);
    if (launchProfile is not null)
    {
        startInfo.ArgumentList.Add("--launch-profile");
        startInfo.ArgumentList.Add(launchProfile);
    }

    startInfo.Environment["Logging__LogLevel__Default"] = "Warning";
    startInfo.Environment[$"Logging__LogLevel__FactoryPulse.{(label == "OPC" ? "OpcUa.Simulator" : "Web.Services.OpcUaTelemetryService")}"] = "Information";
    startInfo.Environment["Logging__LogLevel__Opc.Ua.Configuration.ApplicationInstance"] = "Error";
    startInfo.Environment["Logging__LogLevel__Opc.Ua.SessionEndpoint"] = "Error";

    var process = new Process
    {
        StartInfo = startInfo,
        EnableRaisingEvents = true
    };
    var exitCompletion = new TaskCompletionSource<int>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    process.OutputDataReceived += (_, eventArgs) =>
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            Console.WriteLine($"[{label}] {eventArgs.Data}");
        }
    };
    process.ErrorDataReceived += (_, eventArgs) =>
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            Console.Error.WriteLine($"[{label}:ERROR] {eventArgs.Data}");
        }
    };
    process.Exited += (_, _) => exitCompletion.TrySetResult(process.ExitCode);

    if (!process.Start())
    {
        process.Dispose();
        throw new InvalidOperationException($"Could not start the {label} process.");
    }

    try
    {
        processJob.AddProcess(process);
    }
    catch
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        process.WaitForExit();
        process.Dispose();
        throw;
    }

    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    return new ChildProcess(label, process, exitCompletion.Task);
}

static async Task EnsurePortAvailableAsync(
    int port,
    string description,
    CancellationToken cancellationToken)
{
    using var client = new TcpClient();
    try
    {
        await client.ConnectAsync("localhost", port, cancellationToken);
    }
    catch (SocketException)
    {
        return;
    }

    throw new InvalidOperationException(
        $"The {description} port {port} is already in use. Stop the process using it and try again.");
}

static async Task WaitForTcpAsync(
    string host,
    int port,
    ChildProcess child,
    TimeSpan timeout,
    CancellationToken cancellationToken)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        ThrowIfChildExited(child);
        cancellationToken.ThrowIfCancellationRequested();

        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            return;
        }
        catch (SocketException)
        {
            await Task.Delay(200, cancellationToken);
        }
    }

    throw new TimeoutException($"{child.Label} did not become ready within {timeout.TotalSeconds:N0} seconds.");
}

static async Task WaitForHttpAsync(
    Uri address,
    ChildProcess child,
    TimeSpan timeout,
    CancellationToken cancellationToken)
{
    using var handler = new SocketsHttpHandler { UseProxy = false };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
    var deadline = DateTimeOffset.UtcNow + timeout;

    while (DateTimeOffset.UtcNow < deadline)
    {
        ThrowIfChildExited(child);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var response = await client.GetAsync(address, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return;
            }
        }
        catch (HttpRequestException)
        {
            // The application is still starting.
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The readiness request timed out; retry until the startup deadline.
        }

        await Task.Delay(250, cancellationToken);
    }

    throw new TimeoutException($"{child.Label} did not become ready within {timeout.TotalSeconds:N0} seconds.");
}

static void ThrowIfChildExited(ChildProcess child)
{
    if (child.ExitTask.IsCompleted)
    {
        throw new InvalidOperationException(
            $"{child.Label} exited during startup with code {child.ExitTask.GetAwaiter().GetResult()}.");
    }
}

static async Task SendCommandAsync(ChildProcess child, string command)
{
    if (child.Process.HasExited)
    {
        return;
    }

    await child.Process.StandardInput.WriteLineAsync(command);
    await child.Process.StandardInput.FlushAsync();
}

static async Task StopChildAsync(ChildProcess? child, TimeSpan timeout)
{
    if (child is null)
    {
        return;
    }

    try
    {
        if (!child.Process.HasExited)
        {
            await SendCommandAsync(child, "Q");
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            try
            {
                await child.Process.WaitForExitAsync(timeoutCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"[LAUNCHER] {child.Label} did not stop gracefully; terminating its owned process tree.");
                child.Process.Kill(entireProcessTree: true);
                await child.Process.WaitForExitAsync();
            }
        }
    }
    catch (InvalidOperationException)
    {
        // The process exited between the state check and shutdown signal.
    }
    finally
    {
        child.Process.Dispose();
    }
}

static string ResolveRepositoryRoot(string[] commandLineArguments)
{
    var rootOptionIndex = Array.IndexOf(commandLineArguments, "--repo-root");
    if (rootOptionIndex >= 0 && rootOptionIndex + 1 < commandLineArguments.Length)
    {
        return ValidateRepositoryRoot(commandLineArguments[rootOptionIndex + 1]);
    }

    foreach (var startingPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startingPath));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FactoryPulse.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }
    }

    throw new DirectoryNotFoundException("Could not locate the FactoryPulse repository root.");
}

static string ValidateRepositoryRoot(string path)
{
    var fullPath = Path.GetFullPath(path);
    return File.Exists(Path.Combine(fullPath, "FactoryPulse.sln"))
        ? fullPath
        : throw new DirectoryNotFoundException(
            $"FactoryPulse.sln was not found under '{fullPath}'.");
}

internal sealed record ChildProcess(string Label, Process Process, Task<int> ExitTask);
