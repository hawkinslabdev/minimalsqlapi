using System.Text.Json;
using Serilog;

namespace MinimalSqlReader.Classes;

public static class EndpointHelper
{
    private static readonly string BasePath = Path.Combine(Directory.GetCurrentDirectory(), "endpoints");

    /// <summary>
    /// Loads a single endpoint definition by folder name.
    /// </summary>
    public static EndpointEntity? LoadEndpoint(string endpoint)
    {
        try
        {
            var dir = Path.Combine(BasePath, endpoint);
            var file = Path.Combine(dir, "entity.json");

            if (!File.Exists(file))
            {
                Log.Warning("⚠️ entity.json not found for endpoint: {Endpoint}", endpoint);
                return null;
            }

            var json = File.ReadAllText(file);
            var entity = JsonSerializer.Deserialize<EndpointEntity>(json);

            if (entity == null || string.IsNullOrWhiteSpace(entity.DatabaseObjectName))
            {
                Log.Warning("⚠️ Invalid or missing DatabaseObjectName for endpoint: {Endpoint}", endpoint);
                return null;
            }

            entity.DatabaseSchema ??= "dbo";
            entity.AllowedColumns ??= new List<string>();

            return entity;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error loading endpoint config: {Endpoint}", endpoint);
            return null;
        }
    }

    /// <summary>
    /// Loads all endpoint definitions from /endpoints and logs them.
    /// </summary>
    public static Dictionary<string, EndpointEntity> GetEndpoints(bool silent = false)
    {
        var endpointMap = new Dictionary<string, EndpointEntity>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(BasePath))
        {
            Log.Warning("⚠️ Endpoints folder not found at {BasePath}", BasePath);
            return endpointMap;
        }

        foreach (var dir in Directory.GetDirectories(BasePath))
        {
            var endpointName = Path.GetFileName(dir);
            var entity = LoadEndpoint(endpointName);

            if (entity != null)
            {
                endpointMap[endpointName] = entity;

                if(!silent) {
                    Log.Information("📦 Loaded endpoint '{Endpoint}': {Schema}.{Object} ({Columns})",
                        endpointName,
                        entity.DatabaseSchema,
                        entity.DatabaseObjectName,
                        (entity.AllowedColumns?.Count ?? 0) > 0
                            ? string.Join(", ", entity.AllowedColumns!)
                            : "ALL columns");
                };
            }
        }

        if (!silent) {
            Log.Information("✅ Total loaded endpoints: {Count}", endpointMap.Count);
        }
        return endpointMap;
    }
}

public class EndpointEntity
{
    public string? DatabaseObjectName { get; set; }
    public string? DatabaseSchema { get; set; }
    public List<string>? AllowedColumns { get; set; }
}
