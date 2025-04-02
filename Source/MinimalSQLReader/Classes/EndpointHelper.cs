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
                // Special handling for Webhooks
                if (endpoint.Equals("Webhooks", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("⚠️ entity.json not found for Webhooks, loading default configuration.");
                    return new EndpointEntity
                    {
                        DatabaseObjectName = "DefaultWebhooksHandler", // Placeholder
                        DatabaseSchema = "dbo", // Default schema
                        AllowedMethods = new List<string> { "POST" } // Webhooks only support POST
                    };
                }

                Log.Warning("⚠️ entity.json not found for endpoint: {Endpoint}", endpoint);
                return null;
            }

            var json = File.ReadAllText(file);
            var entity = JsonSerializer.Deserialize<EndpointEntity>(json);

            // Special handling for invalid or missing DatabaseObjectName
            if (entity == null || string.IsNullOrWhiteSpace(entity.DatabaseObjectName))
            {
                if (endpoint.Equals("Webhooks", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("⚠️ Invalid or missing DatabaseObjectName for Webhooks, using default configuration.");
                    return new EndpointEntity
                    {
                        DatabaseObjectName = "DefaultWebhooksHandler", // Placeholder
                        DatabaseSchema = "dbo", // Default schema
                        AllowedMethods = new List<string> { "POST" } // Webhooks only support POST
                    };
                }

                Log.Warning("⚠️ Invalid or missing DatabaseObjectName for endpoint: {Endpoint}", endpoint);
                return null;
            }

            // Default settings for other endpoints
            entity.DatabaseSchema ??= "dbo";

            // Set default AllowedMethods if not provided
            if (entity.AllowedMethods == null || entity.AllowedMethods.Count == 0)
            {
                // If procedure is configured, allow all methods by default
                if (!string.IsNullOrEmpty(entity.Procedure))
                {
                    entity.AllowedMethods = new List<string> { "GET", "POST", "PUT", "DELETE" };
                }
                else
                {
                    // If no procedure, only allow GET
                    entity.AllowedMethods = new List<string> { "GET" };
                }
            }
            else
            {
                // Normalize method names to uppercase
                entity.AllowedMethods = entity.AllowedMethods.Select(m => m.ToUpper()).ToList();
                
                // Validate the methods
                var validMethods = new[] { "GET", "POST", "PUT", "DELETE" };
                var invalidMethods = entity.AllowedMethods.Where(m => !validMethods.Contains(m)).ToList();
                
                if (invalidMethods.Any())
                {
                    Log.Warning("⚠️ Invalid HTTP methods in endpoint {Endpoint}: {Methods}", 
                        endpoint, string.Join(", ", invalidMethods));
                    
                    // Remove invalid methods
                    entity.AllowedMethods = entity.AllowedMethods
                        .Where(m => validMethods.Contains(m))
                        .ToList();
                }
                
                // Ensure a procedure is configured for non-GET methods
                var writeMethods = entity.AllowedMethods.Where(m => m != "GET").ToList();
                if (writeMethods.Any() && string.IsNullOrEmpty(entity.Procedure))
                {
                    Log.Warning("⚠️ Endpoint {Endpoint} allows write methods {Methods} but has no procedure configured. These methods will be disabled.", 
                        endpoint, string.Join(", ", writeMethods));
                    
                    // Remove write methods if no procedure is configured
                    entity.AllowedMethods = entity.AllowedMethods
                        .Where(m => m == "GET")
                        .ToList();
                }
            }

            // Skip AllowedColumns handling for Webhooks
            if (!endpoint.Equals("Webhooks", StringComparison.OrdinalIgnoreCase))
            {
                entity.AllowedColumns ??= new List<string>();
            }

            // Log if this endpoint has a procedure configured
            if (!string.IsNullOrEmpty(entity.Procedure))
            {
                Log.Debug("🔄 Endpoint '{Endpoint}' has procedure configured: {Procedure}, methods: {Methods}", 
                    endpoint, entity.Procedure, string.Join(", ", entity.AllowedMethods));
            }

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
                    Log.Information("📦 Loaded endpoint '{Endpoint}': {Schema}.{Object} ({Columns}){Procedure}",
                        endpointName,
                        entity.DatabaseSchema,
                        entity.DatabaseObjectName,
                        (entity.AllowedColumns?.Count ?? 0) > 0
                            ? string.Join(", ", entity.AllowedColumns!)
                            : "ALL columns",
                        !string.IsNullOrEmpty(entity.Procedure)
                            ? $", Procedure: {entity.Procedure}"
                            : "");
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
    public string? Procedure { get; set; }
    public List<string>? AllowedMethods { get; set; }
}