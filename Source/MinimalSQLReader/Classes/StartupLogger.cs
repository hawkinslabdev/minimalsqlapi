using Microsoft.Extensions.Hosting;
using MinimalSqlReader.Classes;
using Serilog;

namespace MinimalSqlReader.Classes;

public class StartupLogger : IHostedService
{
    private readonly EnvironmentSettings _environmentSettings;

    public StartupLogger(EnvironmentSettings environmentSettings)
    {
        _environmentSettings = environmentSettings;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Log.Information("🚀 Application started. Loading environments and endpoints...");

        // Log environments
        var envRoot = Path.Combine(Directory.GetCurrentDirectory(), "environments");
        if (Directory.Exists(envRoot))
        {
            foreach (var envDir in Directory.GetDirectories(envRoot))
            {
                var envName = Path.GetFileName(envDir);
                try
                {
                    var (connStr, serverName) = _environmentSettings.LoadEnvironmentOrThrow(envName);
                    Log.Information("🌍 Loaded environment: {Env} → {ServerName}, DB=`{Database}`", envName, serverName, connStr);
                }
                catch (Exception ex)
                {
                    Log.Warning("⚠️ Failed to load environment '{Env}': {Error}", envName, ex.Message);
                }
            }
        }

        // Log endpoints
        var endpointRoot = Path.Combine(Directory.GetCurrentDirectory(), "endpoints");
        var endpoints = EndpointHelper.GetEndpoints();
        
        Log.Debug("📦 Loaded {Count} endpoints from '{Path}'", endpoints.Count, endpointRoot);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
