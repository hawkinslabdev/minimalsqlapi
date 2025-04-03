using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using DynamicODataToSQL;
using Dapper;
using Flurl;
using MinimalSqlReader.Classes;
using Serilog;
using System.Text.Json;
using System.Data;

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
            // Step 1: Validate environment
            if (!_environmentSettings.TryLoadEnvironment(env, out var connectionString, out var serverName) || 
                string.IsNullOrEmpty(connectionString))
            {
                return BadRequest(new { error = $"Invalid or missing environment: {env}", success = false });
            }

            // Step 2: Validate endpoint
            var endpointInfo = ValidateAndExtractEndpoint(endpointPath, out var errorResult);
            if (errorResult != null)
            {
                return errorResult;
            }

            // Step 3: Extract endpoint details
            var schema = endpointInfo!.Value.Schema;
            var objectName = endpointInfo.Value.ObjectName;
            var allowedColumns = endpointInfo.Value.AllowedColumns;
            var allowedMethods = endpointInfo.Value.AllowedMethods;

            // Step 4: Check if GET is allowed
            if (!allowedMethods.Contains("GET"))
            {
                return MethodNotAllowed("GET", endpointPath);
            }

            // Step 5: Load columns if needed
            if (allowedColumns.Count == 0)
            {
                allowedColumns = await LoadColumnsFromDatabaseAsync(schema, objectName, connectionString);
                
                if (allowedColumns.Count == 0)
                {
                    var msg = $"❌ No columns found for {schema}.{objectName}. Please verify the object exists and is accessible.";
                    Log.Error(msg);
                    return StatusCode(500, new { error = "Invalid entity. Object definition not correct.", success = false });
                }
            }

            // Step 6: Validate column names
            var invalidColumns = allowedColumns.Where(c => c.Contains(' ')).ToList();
            if (invalidColumns.Any())
            {
                var msg = $"❌ Invalid column names for OData: {string.Join(", ", invalidColumns)}. Column names must not contain spaces.";
                Log.Error(msg);
                throw new InvalidOperationException("Invalid entity. Column names must not contain spaces.");
            }

            // Step 7 Validate and process select parameter to enforce AllowedColumns
            if (!string.IsNullOrEmpty(select))
            {
                // Parse selected columns and check if they're allowed
                var selectedColumns = select.Split(',')
                    .Select(c => c.Trim())
                    .ToList();
                
                var invalidSelectedColumns = selectedColumns
                    .Where(col => !allowedColumns.Contains(col, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                
                if (invalidSelectedColumns.Any())
                {
                    var msg = $"❌ Selected columns not allowed: {string.Join(", ", invalidSelectedColumns)}";
                    Log.Warning(msg);
                    return BadRequest(new { error = $"One or more columns are not allowed: {string.Join(", ", invalidSelectedColumns)}" });
                }
            }
            else if (allowedColumns.Count > 0)
            {
                // No select provided but we have allowed columns - restrict to those columns
                select = string.Join(",", allowedColumns);
                Log.Debug("🔒 No $select provided, restricting to allowed columns: {Columns}", select);
            }

            // Step 8: Build and execute query
            var odataParams = BuildODataParameters(top, skip, select, filter, orderby);
            var (query, parameters) = _oDataToSqlConverter.ConvertToSQL($"{schema}.{objectName}", odataParams);
            query = SanitizeSqlQuery(query, schema, objectName);
            var (rows, isLastPage) = await ExecuteQueryAsync(connectionString, query, parameters, top);

            // Step 9: Build result
            var result = BuildResult(rows, isLastPage, top, env, endpointPath, 
                Request.Query.ContainsKey("$select") ? select : null, filter, orderby, skip);

            Log.Debug("📡 Querying {env}:{schema}.{object} with {select} -> rows: {count}", 
                env, schema, objectName, select ?? "all columns", isLastPage ? rows.Count : rows.Count - 1);

            // Step 10: Return success
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

    [HttpPost(Name = "InsertRecord")]
    public async Task<IActionResult> InsertAsync(
        string env,
        string endpointPath,
        [FromBody] JsonElement data)
    {
        var url = $"{Request.Scheme}://{Request.Host}{Request.Path}";
        Log.Information("📥 POST Request received: {Method} {Url}", Request.Method, url);

        // Validate endpoint and check if POST is allowed
        var endpointInfo = ValidateAndExtractEndpoint(endpointPath, out var errorResult);
        if (errorResult != null)
        {
            return errorResult;
        }

        var (_, _, _, procedure, allowedMethods) = endpointInfo!.Value;

        // Check if POST is allowed for this endpoint
        if (!allowedMethods.Contains("POST"))
        {
            return MethodNotAllowed("POST", endpointPath);
        }

        // Check if procedure is configured
        if (string.IsNullOrEmpty(procedure))
        {
            return BadRequest(new { error = $"Endpoint '{endpointPath}' has POST enabled but no procedure configured." });
        }

        return await ExecuteProcedureAsync(env, endpointPath, data, "INSERT");
    }

    [HttpPut(Name = "UpdateRecord")]
    public async Task<IActionResult> UpdateAsync(
        string env,
        string endpointPath,
        [FromBody] JsonElement data)
    {
        var url = $"{Request.Scheme}://{Request.Host}{Request.Path}";
        Log.Information("📥 PUT Request received: {Method} {Url}", Request.Method, url);

        // Validate endpoint and check if PUT is allowed
        var endpointInfo = ValidateAndExtractEndpoint(endpointPath, out var errorResult);
        if (errorResult != null)
        {
            return errorResult;
        }

        var (_, _, _, procedure, allowedMethods) = endpointInfo!.Value;

        // Check if PUT is allowed for this endpoint
        if (!allowedMethods.Contains("PUT"))
        {
            return MethodNotAllowed("PUT", endpointPath);
        }

        // Check if procedure is configured
        if (string.IsNullOrEmpty(procedure))
        {
            return BadRequest(new { error = $"Endpoint '{endpointPath}' has PUT enabled but no procedure configured." });
        }

        return await ExecuteProcedureAsync(env, endpointPath, data, "UPDATE");
    }

    [HttpDelete(Name = "DeleteRecord")]
    public async Task<IActionResult> DeleteAsync(
        string env,
        string endpointPath,
        [FromQuery] string id)
    {
        var url = $"{Request.Scheme}://{Request.Host}{Request.Path}{Request.QueryString}";
        Log.Information("📥 DELETE Request received: {Method} {Url}", Request.Method, url);

        // Validate endpoint and check if DELETE is allowed
        var endpointInfo = ValidateAndExtractEndpoint(endpointPath, out var errorResult);
        if (errorResult != null)
        {
            return errorResult;
        }

        var (_, _, _, procedure, allowedMethods) = endpointInfo!.Value;

        // Check if DELETE is allowed for this endpoint
        if (!allowedMethods.Contains("DELETE"))
        {
            return MethodNotAllowed("DELETE", endpointPath);
        }

        // Check if procedure is configured
        if (string.IsNullOrEmpty(procedure))
        {
            return BadRequest(new { error = $"Endpoint '{endpointPath}' has DELETE enabled isn't configured.", success = false });
        }

        // For DELETE, we create a simple object with just the ID
        var data = new { id };
        // Convert to JsonElement
        var jsonData = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(data));
        
        return await ExecuteProcedureAsync(env, endpointPath, jsonData, "DELETE");
    }

    private IActionResult MethodNotAllowed(string method, string endpointPath)
    {
        var message = $"HTTP {method} method is not allowed for endpoint '{endpointPath}'";
        Log.Warning("⚠️ {Message}", message);
        return StatusCode(405, new { error = message, success = false });
    }

    private async Task<IActionResult> ExecuteProcedureAsync(
        string env,
        string endpointPath,
        object data,
        string method)
    {
        try
        {
            // Validate environment and connection string
            if (!_environmentSettings.TryLoadEnvironment(env, out var connectionString, out var serverName) || 
                string.IsNullOrEmpty(connectionString))
            {
                return BadRequest(new { error = $"Invalid or missing environment: {env}", success = false });
            }

            // Validate endpoint
            var endpointInfo = ValidateAndExtractEndpoint(endpointPath, out var errorResult);
            if (errorResult != null)
            {
                return errorResult;
            }

            var (schema, objectName, _, procedureName, allowedMethods) = endpointInfo!.Value;

            // Check if method is allowed
            if (!allowedMethods.Contains(method == "INSERT" ? "POST" : 
                                        method == "UPDATE" ? "PUT" : 
                                        method == "DELETE" ? "DELETE" : "GET"))
            {
                return MethodNotAllowed(method, endpointPath);
            }

            // Check if procedure is configured
            if (string.IsNullOrEmpty(procedureName))
            {
                return BadRequest(new { error = $"No procedure configured for endpoint: {endpointPath}", success = false });
            }

            // Split the procedure name into schema and name parts
            var procedureParts = procedureName.Split('.');
            if (procedureParts.Length != 2)
            {
                return BadRequest(new { error = $"Invalid procedure format. Expected 'schema.procedureName', got: {procedureName}", success = false });
            }

            var procedureSchema = procedureParts[0].Trim('[', ']');
            var procedureObjectName = procedureParts[1].Trim('[', ']');

            // Convert data to parameters
            var parameters = ConvertToParameters(data);
            
            // Add Method parameter if not already included
            if (!parameters.ParameterNames.Any(p => p.Equals("@Method", StringComparison.OrdinalIgnoreCase)))
            {
                parameters.Add("@Method", method);
            }

            // Call the stored procedure
            Log.Debug("📡 Executing procedure {schema}.{procedure} with method {method}", 
                procedureSchema, procedureObjectName, method);

            // Execute the stored procedure
            var result = await ExecuteStoredProcedureAsync(
                connectionString, 
                procedureSchema, 
                procedureObjectName, 
                parameters);

            return Ok(new { 
                success = true, 
                message = $"{method} operation completed successfully", 
                result 
            });
        }
        catch (SqlException ex)
        {
            Log.Error(ex, "❌ SQL error while executing procedure for {env}:{endpointPath}", env, endpointPath);
            return StatusCode(500, new { error = $"Database error: {ex.Message}", success = false });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Unexpected error while executing procedure for {env}:{endpointPath}", env, endpointPath);
            return StatusCode(500, new { error = $"Unexpected error: {ex.Message}", success = false });
        }
    }

    private DynamicParameters ConvertToParameters(object data)
    {
        var parameters = new DynamicParameters();
        
        // Try to convert the object to a dictionary
        try {
            if (data is JsonElement jsonElement)
            {
                // Handle JsonElement directly
                if (jsonElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in jsonElement.EnumerateObject())
                    {
                        var paramName = $"@{property.Name}";
                        
                        if (property.Value.ValueKind == JsonValueKind.Null)
                        {
                            parameters.Add(paramName, null);
                        }
                        else if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            parameters.Add(paramName, property.Value.GetString());
                        }
                        else if (property.Value.ValueKind == JsonValueKind.Number)
                        {
                            // Check if it's an integer or decimal
                            if (property.Value.TryGetInt32(out int intValue))
                            {
                                parameters.Add(paramName, intValue);
                            }
                            else if (property.Value.TryGetInt64(out long longValue))
                            {
                                parameters.Add(paramName, longValue);
                            }
                            else if (property.Value.TryGetDouble(out double doubleValue))
                            {
                                parameters.Add(paramName, doubleValue);
                            }
                            else if (property.Value.TryGetDecimal(out decimal decimalValue))
                            {
                                parameters.Add(paramName, decimalValue);
                            }
                        }
                        else if (property.Value.ValueKind == JsonValueKind.True || 
                                property.Value.ValueKind == JsonValueKind.False)
                        {
                            parameters.Add(paramName, property.Value.GetBoolean());
                        }
                        else if (property.Value.ValueKind == JsonValueKind.Array)
                        {
                            // Convert array to string for simplicity
                            parameters.Add(paramName, property.Value.ToString());
                        }
                        else
                        {
                            // For other types, convert to string
                            parameters.Add(paramName, property.Value.ToString());
                        }
                    }
                }
            }
            else
            {
                // For non-JsonElement objects, serialize and deserialize
                var json = JsonSerializer.Serialize(data);
                var dictionary = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

                if (dictionary != null)
                {
                    foreach (var kvp in dictionary)
                    {
                        var paramName = $"@{kvp.Key}";
                        
                        if (kvp.Value.ValueKind == JsonValueKind.Null)
                        {
                            parameters.Add(paramName, null);
                        }
                        else if (kvp.Value.ValueKind == JsonValueKind.String)
                        {
                            parameters.Add(paramName, kvp.Value.GetString());
                        }
                        else if (kvp.Value.ValueKind == JsonValueKind.Number)
                        {
                            // Check if it's an integer or decimal
                            if (kvp.Value.TryGetInt32(out int intValue))
                            {
                                parameters.Add(paramName, intValue);
                            }
                            else if (kvp.Value.TryGetInt64(out long longValue))
                            {
                                parameters.Add(paramName, longValue);
                            }
                            else if (kvp.Value.TryGetDouble(out double doubleValue))
                            {
                                parameters.Add(paramName, doubleValue);
                            }
                            else if (kvp.Value.TryGetDecimal(out decimal decimalValue))
                            {
                                parameters.Add(paramName, decimalValue);
                            }
                        }
                        else if (kvp.Value.ValueKind == JsonValueKind.True || 
                                kvp.Value.ValueKind == JsonValueKind.False)
                        {
                            parameters.Add(paramName, kvp.Value.GetBoolean());
                        }
                        else if (kvp.Value.ValueKind == JsonValueKind.Array)
                        {
                            // Convert array to string for simplicity
                            parameters.Add(paramName, kvp.Value.ToString());
                        }
                        else
                        {
                            // For other types, convert to string
                            parameters.Add(paramName, kvp.Value.ToString());
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error converting parameters: {ErrorMessage}", ex.Message);
            throw new InvalidOperationException($"Error converting parameters: {ex.Message}", ex);
        }

        return parameters;
    }

    private async Task<object> ExecuteStoredProcedureAsync(
        string connectionString,
        string schema,
        string procedureName,
        DynamicParameters parameters)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        // Execute the stored procedure
        var result = await conn.QueryAsync<dynamic>(
            $"[{schema}].[{procedureName}]", 
            parameters, 
            commandType: CommandType.StoredProcedure);

        return result;
    }

    private (string Schema, string ObjectName, List<string> AllowedColumns, string? ProcedureName, List<string> AllowedMethods)? ValidateAndExtractEndpoint(
        string endpointPath, 
        out IActionResult? errorResult)
    {
        errorResult = null;
        
        // Validate endpoint path
        if (string.IsNullOrWhiteSpace(endpointPath))
        {
            errorResult = BadRequest(new { error = "Missing endpoint path in the request.", success = false });
            return null;
        }

        // Parse and validate endpoint
        var endpointParts = endpointPath.Split('/');
        if (endpointParts.Length == 0)
        {
            errorResult = BadRequest(new { error = "Invalid endpoint format.", success = false });
            return null;
        }

        var endpointName = endpointParts[0];
        var endpointEntity = EndpointHelper.LoadEndpoint(endpointName);
        if (endpointEntity == null)
        {
            errorResult = NotFound(new { error = $"Endpoint '{endpointName}' not found.", success = false });
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
        
        // Get the stored procedure name if it exists
        var procedureName = endpointEntity.Procedure;
        
        // Get allowed methods
        var allowedMethods = endpointEntity.AllowedMethods ?? new List<string> { "GET" };
        
        return (schema, objectName, allowedColumns, procedureName, allowedMethods);
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