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

            // Validate endpoint
            var endpointInfo = ValidateAndExtractEndpoint(endpointPath, out var errorResult);
            if (errorResult != null)
            {
                return errorResult;
            }

            var (schema, objectName, allowedColumns) = endpointInfo!.Value;

            // Load columns if needed
            if (allowedColumns.Count == 0)
            {
                allowedColumns = await LoadColumnsFromDatabaseAsync(schema, objectName, connectionString);
                
                if (allowedColumns.Count == 0)
                {
                    var msg = $"❌ No columns found for {schema}.{objectName}. Please verify the object exists and is accessible.";
                    Log.Error(msg);
                    return StatusCode(500, new { error = "Invalid entity. Object definition not correct." });
                }
            }

            // Validate column names for OData compatibility
            var invalidColumns = allowedColumns.Where(c => c.Contains(' ')).ToList();
            if (invalidColumns.Any())
            {
                var msg = $"❌ Invalid column names for OData: {string.Join(", ", invalidColumns)}. Column names must not contain spaces.";
                Log.Error(msg);
                throw new InvalidOperationException("Invalid entity. Column names must not contain spaces.");
            }

            // Build OData parameters
            var odataParams = BuildODataParameters(top, skip, select, filter, orderby);

            // Convert OData to SQL and sanitize
            var (query, parameters) = _oDataToSqlConverter.ConvertToSQL($"{schema}.{objectName}", odataParams);
            query = SanitizeSqlQuery(query, schema, objectName);

            // Execute query
            var (rows, isLastPage) = await ExecuteQueryAsync(connectionString, query, parameters, top);

            // Build and return result
            var result = BuildResult(rows, isLastPage, top, env, endpointPath, 
                Request.Query.ContainsKey("$select") ? select : null, filter, orderby, skip);

            Log.Debug("📡 Querying {env}:{schema}.{object} with {select} -> rows: {count}", 
                env, schema, objectName, select ?? "all columns", isLastPage ? rows.Count : rows.Count - 1);

            return Ok(result);
        }
        catch (SqlException ex)
        {
            Log.Error(ex, "❌ SQL error while querying {env}:{endpointPath}", env, endpointPath);
            return StatusCode(500, new { error = "Internal connection error." });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Unexpected error while querying {env}:{endpointPath}", env, endpointPath);
            return StatusCode(500, new { error = $"Unexpected error: {ex.Message}" });
        }
    }

    private (string Schema, string ObjectName, List<string> AllowedColumns)? ValidateAndExtractEndpoint(
        string endpointPath, 
        out IActionResult? errorResult)
    {
        errorResult = null;
        
        // Validate endpoint path
        if (string.IsNullOrWhiteSpace(endpointPath))
        {
            errorResult = BadRequest(new { error = "Missing endpoint path in the request." });
            return null;
        }

        // Parse and validate endpoint
        var endpointParts = endpointPath.Split('/');
        if (endpointParts.Length == 0)
        {
            errorResult = BadRequest(new { error = "Invalid endpoint format." });
            return null;
        }

        var endpointName = endpointParts[0];
        var endpointEntity = EndpointHelper.LoadEndpoint(endpointName);
        if (endpointEntity == null)
        {
            errorResult = NotFound(new { error = $"Endpoint '{endpointName}' not found." });
            return null;
        }

        // Extract database object details
        var objectName = !string.IsNullOrEmpty(endpointEntity.DatabaseObjectName) 
            ? endpointEntity.DatabaseObjectName.Trim('[', ']') 
            : "";
        
        var schema = !string.IsNullOrEmpty(endpointEntity.DatabaseSchema) 
            ? endpointEntity.DatabaseSchema.Trim('[', ']') 
            : "dbo";
        
        var allowedColumns = endpointEntity.AllowedColumns ?? new List<string>();
        
        return (schema, objectName, allowedColumns);
    }

    private Dictionary<string, string> BuildODataParameters(
        int top, 
        int skip, 
        string? select, 
        string? filter, 
        string? orderby)
    {
        var odataParams = new Dictionary<string, string>
        {
            { "top", (top + 1).ToString() },
            { "skip", skip.ToString() }
        };

        if (!string.IsNullOrEmpty(select)) odataParams.Add("select", select);
        if (!string.IsNullOrEmpty(filter)) odataParams.Add("filter", filter);
        if (!string.IsNullOrEmpty(orderby)) odataParams.Add("orderby", orderby);
        
        return odataParams;
    }

    private string SanitizeSqlQuery(string query, string schema, string objectName)
    {
        // Replace FROM [dbo].[Table] => FROM [dbo].[Table] WITH (NOLOCK)
        var fromPattern = $"FROM [{schema}].[{objectName}]";
        var fromWithNoLock = $"{fromPattern} WITH (NOLOCK)";
        return query.Replace(fromPattern, fromWithNoLock, StringComparison.InvariantCultureIgnoreCase);
    }

    private async Task<(List<dynamic> Rows, bool IsLastPage)> ExecuteQueryAsync(
        string connectionString,
        string query,
        object parameters,
        int top)
    {
        await using var conn = new SqlConnection(connectionString);
        var rows = (await conn.QueryAsync(query, parameters)).ToList();
        var isLastPage = rows.Count <= top;
        
        return (rows, isLastPage);
    }

    private object BuildResult(
        List<dynamic> rows, 
        bool isLastPage, 
        int top, 
        string env, 
        string endpointPath, 
        string? select, 
        string? filter, 
        string? orderby, 
        int skip)
    {
        return new
        {
            Count = isLastPage ? rows.Count : rows.Count - 1,
            Value = rows.Take(top),
            NextLink = isLastPage
                ? null
                : BuildNextLink(env, endpointPath, select, filter, orderby, top, skip)
        };
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