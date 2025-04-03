using Microsoft.Extensions.Hosting;
using MinimalSqlReader.Interfaces; 
using Serilog;

namespace MinimalSqlReader.Classes;

public class StartupLogger : IHostedService
{
    private readonly IEnvironmentSettingsProvider _environmentSettings;

    public StartupLogger(IEnvironmentSettingsProvider environmentSettings)
    {
        _environmentSettings = environmentSettings;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
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
                    // Changed from LoadEnvironmentOrThrow to LoadEnvironmentOrThrowAsync and added await
                    var (connectionString, serverName) = await _environmentSettings.LoadEnvironmentOrThrowAsync(envName);
                    Log.Information("🌍 Loaded environment: {Env} → {ServerName}, DB=`{Database}`", envName, serverName, connectionString);
                }
                catch (Exception ex)
                {
                    Log.Warning("⚠️ Failed to load environment '{Env}': {Error}", envName, ex.Message);
                }
            }
        }

        // Log endpoints
        var endpointRoot = Path.Combine(Directory.GetCurrentDirectory(), "endpoints");
        var endpoints = EndpointHelper.GetEndpoints(silent: true);
        
        Log.Debug("📦 Loaded {Count} endpoints from '{Path}'", endpoints.Count, endpointRoot);

        return;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}