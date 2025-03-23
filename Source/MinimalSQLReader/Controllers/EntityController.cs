using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using DynamicODataToSQL;
using Dapper;
using Flurl;
using MinimalSqlReader.Classes;
using Serilog;

namespace MinimalSqlReader.Controllers;

[ApiController]
[Route("api/{env}/{**endpointPath}")]
public class DatabaseObjectsController : ControllerBase
{
    private readonly IODataToSqlConverter _oDataToSqlConverter;
    private readonly EnvironmentSettings _environmentSettings;

    public DatabaseObjectsController(IODataToSqlConverter oDataToSqlConverter, EnvironmentSettings environmentSettings)
    {
        _oDataToSqlConverter = oDataToSqlConverter;
        _environmentSettings = environmentSettings;
        Log.Debug("🚀 DatabaseObjectsController constructor triggered");
    }

    [HttpGet(Name = "QueryRecords")]
    public async Task<IActionResult> QueryAsync(
        string env,
        string endpointPath,
        [FromQuery(Name = "$select")] string? select = null,
        [FromQuery(Name = "$filter")] string? filter = null,
        [FromQuery(Name = "$orderby")] string? orderby = null,
        [FromQuery(Name = "$top")] int top = 10,
        [FromQuery(Name = "$skip")] int skip = 0)
    {
        var url = $"{Request.Scheme}://{Request.Host}{Request.Path}{Request.QueryString}";
        Log.Information("📥 Request received: {Method} {Url}", Request.Method, url);

        try
        {
            // Validate environment and connection string
            if (!_environmentSettings.TryLoadEnvironment(env, out var connectionString, out var serverName) || 
                string.IsNullOrEmpty(connectionString))
            {
                return BadRequest(new { error = $"Invalid or missing environment: {env}" });
            }

            // Validate endpoint path
            if (string.IsNullOrWhiteSpace(endpointPath))
            {
                return BadRequest(new { error = "Missing endpoint path in the request." });
            }

            // Parse and validate endpoint
            var endpointParts = endpointPath.Split('/');
            if (endpointParts.Length == 0)
            {
                return BadRequest(new { error = "Invalid endpoint format." });
            }

            var endpointName = endpointParts[0];
            var endpointEntity = EndpointHelper.LoadEndpoint(endpointName);
            if (endpointEntity == null)
            {
                return NotFound(new { error = $"Endpoint '{endpointName}' not found." });
            }

            // Extract database object details
            var objectName = !string.IsNullOrEmpty(endpointEntity.DatabaseObjectName) 
                ? endpointEntity.DatabaseObjectName.Trim('[', ']') 
                : "";
            
            var schema = !string.IsNullOrEmpty(endpointEntity.DatabaseSchema) 
                ? endpointEntity.DatabaseSchema.Trim('[', ']') 
                : "dbo";
            
            var allowedColumns = endpointEntity.AllowedColumns ?? new List<string>();

            // Load columns if not specified in endpoint configuration
            if (allowedColumns.Count == 0)
            {
                allowedColumns = await LoadColumnsFromDatabaseAsync(schema, objectName, connectionString);
            }

            if (allowedColumns.Count == 0)
            {
                var msg = $"❌ No columns found for {schema}.{objectName}. Please verify the object exists and is accessible.";
                var clientMsg = $"Invalid entity. Object definition not correct.";
                Log.Error(msg);
                return StatusCode(500, new { error = clientMsg });
            }

            // Validate column names for OData compatibility
            var invalidColumns = allowedColumns.Where(c => c.Contains(' ')).ToList();
            if (invalidColumns.Any())
            {
                var msg = $"❌ Invalid column names for OData: {string.Join(", ", invalidColumns)}. Column names must not contain spaces.";
                var clientMsg = $"Invalid entity. Column names must not contain spaces.";

                Log.Error(msg);
                throw new InvalidOperationException(clientMsg);
            }

            // Build OData parameters dictionary with null safety
            var odataParams = new Dictionary<string, string>
            {
                { "top", (top + 1).ToString() },
                { "skip", skip.ToString() }
            };

            if (!string.IsNullOrEmpty(select)) odataParams.Add("select", select);
            if (!string.IsNullOrEmpty(filter)) odataParams.Add("filter", filter);
            if (!string.IsNullOrEmpty(orderby)) odataParams.Add("orderby", orderby);

            // Convert OData to SQL
            var (query, parameters) = _oDataToSqlConverter.ConvertToSQL($"{schema}.{objectName}", odataParams);

            // Replace FROM [dbo].[Table] => FROM [dbo].[Table] WITH (NOLOCK)
            var fromPattern = $"FROM [{schema}].[{objectName}]";
            var fromWithNoLock = $"{fromPattern} WITH (NOLOCK)";
            query = query.Replace(fromPattern, fromWithNoLock, StringComparison.InvariantCultureIgnoreCase);

            // Execute the query
            await using var conn = new SqlConnection(connectionString);
            var rows = (await conn.QueryAsync(query, parameters)).ToList();
            var isLastPage = rows.Count <= top;

            // Build result
            var result = new
            {
                Count = isLastPage ? rows.Count : rows.Count - 1,
                Value = rows.Take(top),
                NextLink = isLastPage
                    ? null
                    : BuildNextLink(env, endpointPath, Request.Query.ContainsKey("$select") ? select : null, filter, orderby, top, skip)
            };

            Log.Debug("📡 Querying {env}:{schema}.{object} with {select} -> rows: {count}", 
                env, schema, objectName, select ?? "all columns", result.Count);

            return Ok(result);
        }
        catch (SqlException ex)
        {
            Log.Error(ex, "❌ SQL error while querying {env}:{endpointPath}", env, endpointPath);
            var clientMsg = $"Internal connection error.";
            return StatusCode(500, new { error = clientMsg });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Unexpected error while querying {env}:{endpointPath}", env, endpointPath);
            return StatusCode(500, new { error = $"Unexpected error: {ex.Message}" });
        }
    }

    private async Task<List<string>> LoadColumnsFromDatabaseAsync(string schema, string objectName, string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            Log.Error("❌ Cannot load columns: connectionString is null or empty");
            return new List<string>();
        }

        if (string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(objectName))
        {
            Log.Error("❌ Cannot load columns: schema or objectName is null or empty");
            return new List<string>();
        }

        Log.Debug("📥 AllowedColumns empty — loading all columns from INFORMATION_SCHEMA for {Schema}.{Object}", 
            schema, objectName);

        var sql = @"
            SELECT c.name AS COLUMN_NAME
            FROM sys.columns c
            INNER JOIN sys.objects o ON c.object_id = o.object_id
            WHERE o.name = @databaseObject AND SCHEMA_NAME(o.schema_id) = @schema";

        try
        {
            await using var tempConn = new SqlConnection(connectionString);
            var result = await tempConn.QueryAsync<string>(sql, new { schema, databaseObject = objectName });
            return result.ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error loading columns for {Schema}.{Object}", schema, objectName);
            return new List<string>();
        }
    }

    private string BuildNextLink(
        string env, 
        string endpointPath, 
        string? select, 
        string? filter, 
        string? orderby, 
        int top, 
        int skip)
    {
        var nextLink = Url.Link("QueryRecords", new { env, endpointPath }) ?? 
            $"/api/{env}/{endpointPath}";

        var url = nextLink
            .SetQueryParam("$top", top)
            .SetQueryParam("$skip", skip + top);

        if (!string.IsNullOrWhiteSpace(select))
            url = url.SetQueryParam("$select", select);
        if (!string.IsNullOrWhiteSpace(filter))
            url = url.SetQueryParam("$filter", filter);
        if (!string.IsNullOrWhiteSpace(orderby))
            url = url.SetQueryParam("$orderby", orderby);

        return url;
    }
}