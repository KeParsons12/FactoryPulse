using FactoryPulse.OpcUa.Simulator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHostedService<MachineSimulationWorker>();
builder.Services.AddHostedService<ConsoleControlService>();
builder.Services
    .AddOpcUa()
    .AddServer(options =>
    {
        options.ApplicationName = "FactoryPulse OPC UA Simulator";
        options.ApplicationUri = "urn:localhost:FactoryPulse:OpcUa:Simulator";
        options.ProductUri = "urn:factorypulse:opcua:simulator";
        options.EndpointUrls.Add("opc.tcp://localhost:62560/FactoryPulse");
        options.PkiRoot = Path.Combine(builder.Environment.ContentRootPath, "pki");
    })
    .AddNodeManager<FactoryPulseMachine.FactoryPulseMachineNodeManagerFactory>();

await builder.Build().RunAsync();
