using FactoryPulse.Web.Components;
using FactoryPulse.Web.Data;
using FactoryPulse.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Opc.Ua;
using Opc.Ua.Client;
using FactoryPulseOpcUaOptions = FactoryPulse.Web.Services.OpcUaClientOptions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(
        builder.Environment.ContentRootPath,
        "App_Data",
        "DataProtectionKeys")))
    .SetApplicationName("FactoryPulse");

if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi();
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<DemoConsoleShutdownService>();
builder.Services.Configure<FactoryPulseOpcUaOptions>(
    builder.Configuration.GetSection(FactoryPulseOpcUaOptions.ConfigurationSection));
var opcUaConfiguration = builder.Configuration
    .GetSection(FactoryPulseOpcUaOptions.ConfigurationSection)
    .Get<FactoryPulseOpcUaOptions>() ?? new FactoryPulseOpcUaOptions();
builder.Services.AddSingleton<OpcUaTelemetryStore>();
builder.Services.AddSingleton<IOpcUaTelemetry>(services =>
    services.GetRequiredService<OpcUaTelemetryStore>());
builder.Services.AddSingleton<OpcUaTelemetryService>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<OpcUaTelemetryService>());
builder.Services
    .AddOpcUa()
    .AddClient(options =>
    {
        options.ApplicationName = "FactoryPulse OPC UA Client";
        options.ApplicationUri = "urn:localhost:FactoryPulse:OpcUa:Client";
        options.ProductUri = "urn:factorypulse:opcua:client";
        options.PkiRoot = Path.Combine(
            builder.Environment.ContentRootPath,
            "App_Data",
            "OpcUa",
            "pki");
        options.Session = new ManagedSessionOptions
        {
            ReconnectPolicy = new ReconnectPolicyOptions
            {
                MaxRetries = opcUaConfiguration.MaximumSessionReconnectAttempts
            }
        };
    })
    .AddDiscoveryAndConnect(options =>
    {
        options.DiscoveryUrl = opcUaConfiguration.Endpoint;
        options.SecurityMode = MessageSecurityMode.SignAndEncrypt;
        options.SecurityPolicyUri = SecurityPolicies.Basic256Sha256;
    });
var databasePath = GetDatabasePath(builder.Configuration, builder.Environment);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
var sqliteConnectionString = new SqliteConnectionStringBuilder
{
    DataSource = databasePath,
    Mode = SqliteOpenMode.ReadWriteCreate,
    Cache = SqliteCacheMode.Shared
}.ToString();

builder.Services.AddDbContextFactory<FactoryPulseDbContext>(options =>
    options.UseSqlite(sqliteConnectionString));
builder.Services.Configure<ProductionEventStoreOptions>(
    builder.Configuration.GetSection(ProductionEventStoreOptions.ConfigurationSection));
builder.Services.AddSingleton<ProductionEventStore>();
builder.Services.AddSingleton<IProductionEventStore>(services =>
    services.GetRequiredService<ProductionEventStore>());
builder.Services.AddHostedService(services =>
    services.GetRequiredService<ProductionEventStore>());
builder.Services.AddSingleton<MonitoringSession>();
builder.Services.AddSingleton<MonitoringSessionService>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<MonitoringSessionService>());
builder.Services.AddSingleton<IProductionAnalyticsService, ProductionAnalyticsService>();
builder.Services.AddSingleton<MachineSimulator>();
builder.Services.AddSingleton<IMachineSimulation>(services =>
    services.GetRequiredService<MachineSimulator>());
builder.Services.AddHostedService(services =>
    services.GetRequiredService<MachineSimulator>());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string GetDatabasePath(IConfiguration configuration, IWebHostEnvironment environment)
{
    var configuredPath = configuration["FactoryPulse:DatabasePath"];

    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(configuredPath, environment.ContentRootPath);
    }

    var localApplicationData = Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData);
    return Path.Combine(localApplicationData, "FactoryPulse", "factorypulse.db");
}
