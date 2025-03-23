using Microsoft.AspNetCore.Mvc;
using MinimalSqlReader.Classes;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Serilog;
using Dapper;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;

namespace MinimalSqlReader.Controllers;

[ApiController]
[Route("webhook/{env}/{webhookId}")]
public class WebhookController : ControllerBase
{
    private readonly EnvironmentSettings _environmentSettings;
    private readonly Dictionary<string, dynamic> _endpointConfigCache = new();
    private static readonly Regex _validIdentifierRegex = new Regex(@"^[a-zA-Z0-9_]+$", RegexOptions.Compiled);

    public WebhookController(EnvironmentSettings environmentSettings)
    {
        _environmentSettings = environmentSettings;
    }

    [HttpPost]
    public async Task<IActionResult> HandleWebhook(string env, string webhookId, [FromBody] JsonElement payload)
    {
        var requestUrl = $"{Request.Scheme}://{Request.Host}{Request.Path}";
        Log.Debug("📥 Webhook received: {Method} {Url}", Request.Method, requestUrl);

        try
        {
            // Validate environment
            if (!_environmentSettings.TryLoadEnvironment(env, out var connectionString, out var serverName))
            {
                return BadRequest(new { error = $"Environment '{env}' is invalid or missing." });
            }

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return BadRequest(new { error = "Database connection string is invalid or missing." });
            }

            // Load and validate endpoint configuration
            var endpointConfig = GetEndpointConfiguration("Webhooks");
            if (endpointConfig == null)
            {
                return NotFound(new { error = "Webhooks endpoint is not configured properly." });
            }

            // Get table name and schema from the configuration
            var tableName = endpointConfig.DatabaseObjectName;
            var schema = endpointConfig.DatabaseSchema ?? "dbo"; // Default to "dbo" if schema is not specified

            if (string.IsNullOrWhiteSpace(tableName))
            {
                Log.Warning("❌ Table name is missing in the configuration.");
                return BadRequest(new { error = "Table name is missing in the configuration." });
            }

            // Validate webhook ID - Fixed the LINQ issue with AllowedColumns
            var allowedColumns = endpointConfig.AllowedColumns as IEnumerable<object>;
            if (allowedColumns != null && allowedColumns.Count() > 0 && 
                !allowedColumns.Any(col => string.Equals(col.ToString(), webhookId, StringComparison.OrdinalIgnoreCase)))
            {
                var allowedList = string.Join(", ", allowedColumns.Select(c => c.ToString()));
                Log.Warning("❌ Webhook ID '{WebhookId}' is not in the allowed list: {AllowedWebhooks}",
                    webhookId, allowedList);
                return NotFound(new { error = $"Webhook ID '{webhookId}' is not configured." });
            }

            // Validate schema and table names to prevent SQL injection
            if (!IsValidSqlIdentifier(schema) || !IsValidSqlIdentifier(tableName))
            {
                Log.Warning("❌ Invalid schema or table name: {Schema}.{TableName}", schema, tableName);
                return BadRequest(new { error = "Invalid schema or table name." });
            }

            // Ensure table exists and insert data
            await EnsureTableExistsAsync(connectionString, schema, tableName);
            var insertedId = await InsertWebhookDataAsync(connectionString, schema, tableName, webhookId, payload);

            Log.Information("✅ Webhook processed successfully: {WebhookId}, InsertedId: {InsertedId}", webhookId, insertedId);
            return Ok(new
            {
                message = "Webhook processed successfully.",
                id = insertedId
            });
        }
        catch (SqlException ex) when (ex.Message.Contains("Timeout expired"))
        {
            Log.Error(ex, "❌ Database timeout error occurred");
            return StatusCode(503, new { error = "Database timeout occurred. Please try again later." });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing webhook {WebhookId}", webhookId);
            return StatusCode(500, new { error = "An error occurred while processing the webhook." });
        }
    }

    private dynamic GetEndpointConfiguration(string endpointName)
    {
        // Cache the endpoint configuration to avoid loading it on every request
        if (!_endpointConfigCache.TryGetValue(endpointName, out var config))
        {
            config = EndpointHelper.LoadEndpoint(endpointName);
            if (config != null)
            {
                _endpointConfigCache[endpointName] = config;
            }
        }
        return config ?? new object();
    }

    private bool IsValidSqlIdentifier(string identifier)
    {
        // Simple validation for SQL identifiers to prevent SQL injection
        return !string.IsNullOrWhiteSpace(identifier) && _validIdentifierRegex.IsMatch(identifier);
    }

    private async Task EnsureTableExistsAsync(string connectionString, string schema, string tableName)
    {
        try
        {
            // First check if table exists
            var tableCheck = @"
                SELECT COUNT(1) FROM sys.tables t 
                JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE t.name = @TableName AND s.name = @Schema";

            await using var connection = new SqlConnection(connectionString);
            var tableExists = await connection.ExecuteScalarAsync<int>(tableCheck, new { Schema = schema, TableName = tableName }) > 0;

            if (!tableExists)
            {
                // Create the table if it doesn't exist
                // Use parameterized TableName and Schema where possible and validated identifiers elsewhere
                var createTableSql = $@"
                    CREATE TABLE [{schema}].[{tableName}] (
                        Id INT IDENTITY(1,1) PRIMARY KEY,
                        WebhookId NVARCHAR(100) NOT NULL,
                        Payload NVARCHAR(MAX) NOT NULL,
                        ReceivedAt DATETIME NOT NULL,
                        Processed BIT DEFAULT 0,
                        ProcessedAt DATETIME NULL
                    );
                    
                    CREATE INDEX IX_{tableName}_WebhookId ON [{schema}].[{tableName}](WebhookId);
                    CREATE INDEX IX_{tableName}_Processed ON [{schema}].[{tableName}](Processed);";

                await connection.ExecuteAsync(createTableSql);
                Log.Information("Created table [{Schema}].[{TableName}]", schema, tableName);
            }
        }
        catch (SqlException ex)
        {
            Log.Error(ex, "❌ Error checking/creating the table '{TableName}' in schema '{Schema}'", tableName, schema);
            throw; // Re-throw for consistent error handling
        }
    }

    private async Task<int> InsertWebhookDataAsync(string connectionString, string schema, string tableName, string webhookId, JsonElement payload)
    {
        var insertQuery = $@"
            INSERT INTO [{schema}].[{tableName}] (WebhookId, Payload, ReceivedAt)
            OUTPUT INSERTED.Id
            VALUES (@WebhookId, @Payload, @ReceivedAt)";

        await using var connection = new SqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<int>(insertQuery, new
        {
            WebhookId = webhookId,
            Payload = payload.ToString(),
            ReceivedAt = DateTime.UtcNow
        });
    }
}