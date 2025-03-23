using System.Text.Json;
using Serilog;

namespace MinimalSqlReader.Classes;

public class EnvironmentSettings
{
    private readonly string _basePath = Path.Combine(Directory.GetCurrentDirectory(), "environments");

    /// <summary>
    /// Loads the environment or throws an exception if invalid
    /// </summary>
    public (string ConnectionString, string ServerName) LoadEnvironmentOrThrow(string env)
    {
        var settingsPath = Path.Combine(_basePath, env, "settings.json");

        if (!File.Exists(settingsPath))
        {
            var message = $"Environment folder or settings.json not found for environment: {env}";
            Log.Error("❌ {Message}", message);
            throw new FileNotFoundException(message);
        }

        try
        {
            var json = File.ReadAllText(settingsPath);
            var config = JsonSerializer.Deserialize<EnvironmentConfig>(json);

            if (config == null || string.IsNullOrWhiteSpace(config.ConnectionString))
            {
                var message = $"Missing or invalid connection string for environment: {env}";
                Log.Error("❌ {Message}", message);
                throw new InvalidOperationException(message);
            }

            var connectionString = config.ConnectionString;
            var serverName = config.ServerName ?? ".";

            Log.Debug("🔌 Loaded database context: `{ConnectionString}`", connectionString);
            
            return (connectionString, serverName);
        }
        catch (JsonException ex)
        {
            Log.Error(ex, "❌ Invalid JSON format in settings.json for environment: {Env}", env);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Unexpected error loading settings for environment: {Env}", env);
            throw;
        }
    }

    /// <summary>
    /// Safe wrapper: returns false instead of throwing
    /// </summary>
    public bool TryLoadEnvironment(string env, out string? connectionString, out string? serverName)
    {
        connectionString = null;
        serverName = ".";

        try
        {
            (connectionString, serverName) = LoadEnvironmentOrThrow(env);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private class EnvironmentConfig
    {
        public string? ConnectionString { get; set; }
        public string? ServerName { get; set; }
    }
}
