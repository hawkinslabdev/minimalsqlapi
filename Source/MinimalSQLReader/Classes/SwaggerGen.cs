using System.Text.Json;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using MinimalSqlReader.Classes;
using Serilog;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MinimalSqlReader.Swagger;

public class MinimalSqlReaderDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        var endpoints = EndpointHelper.GetEndpoints(silent: true).ToDictionary(kvp => kvp.Key, kvp => (dynamic)kvp.Value);
        var environments = GetEnvironmentNames();

        // Remove all controller-discovered paths
        var controllerDiscoveredPaths = context.ApiDescriptions
            .Where(desc => desc.ActionDescriptor.RouteValues.ContainsKey("controller"))
            .Select(desc => desc.RelativePath)
            .Distinct()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        foreach (var path in controllerDiscoveredPaths)
        {
            var normalizedPath = "/" + path!.Split('?')[0].TrimEnd('/');
            if (swaggerDoc.Paths.ContainsKey(normalizedPath))
            {
                swaggerDoc.Paths.Remove(normalizedPath);
            }
        }

        // Re-add standard API endpoints
        AddStandardApiEndpoints(swaggerDoc, endpoints, environments);
        
        // Add webhook endpoints (POST only)
        AddWebhookEndpoints(swaggerDoc, environments);
    }

    private void AddStandardApiEndpoints(OpenApiDocument swaggerDoc, Dictionary<string, dynamic> endpoints, List<string> environments)
    {
        foreach (var (endpointName, entity) in endpoints)
        {
            if (endpointName == "Webhooks")
            continue;

            var path = $"/api/{{env}}/{endpointName}";
            if (!swaggerDoc.Paths.ContainsKey(path))
                swaggerDoc.Paths[path] = new OpenApiPathItem();

            var operation = new OpenApiOperation
            {
                Tags = new List<OpenApiTag> { new() { Name = endpointName } },
                Summary = $"GET {endpointName} records",
                Description = $"Query the {endpointName} endpoint using OData filters",
                OperationId = $"get_{endpointName}".ToLowerInvariant(),
                Parameters = new List<OpenApiParameter>
                {
                    new()
                    {
                        Name = "env",
                        In = ParameterLocation.Path,
                        Required = true,
                        Schema = new OpenApiSchema
                        {
                            Type = "string",
                            Enum = environments.Select(e => new OpenApiString(e!)).Cast<IOpenApiAny>().ToList()
                        },
                        Description = "Target environment"
                    },
                    new()
                    {
                        Name = "$filter",
                        In = ParameterLocation.Query,
                        Required = false,
                        Schema = new OpenApiSchema { Type = "string" },
                        Description = "OData $filter"
                    },
                    new()
                    {
                        Name = "$select",
                        In = ParameterLocation.Query,
                        Required = false,
                        Schema = new OpenApiSchema { Type = "string" },
                        Description = "OData $select"
                    }
                },
                Responses = new OpenApiResponses
                {
                    ["200"] = new OpenApiResponse { Description = "Success" },
                    ["400"] = new OpenApiResponse { Description = "Bad Request" },
                    ["404"] = new OpenApiResponse { Description = "Not Found" },
                    ["500"] = new OpenApiResponse { Description = "Server Error" }
                }
            };

            swaggerDoc.Paths[path].Operations[OperationType.Get] = operation;
        }
    }

    private void AddWebhookEndpoints(OpenApiDocument swaggerDoc, List<string> environments)
    {
        var webhookEndpointConfig = EndpointHelper.LoadEndpoint("Webhooks");
        if (webhookEndpointConfig == null)
        {
            Log.Warning("⚠️ Webhooks endpoint configuration not found. Skipping Swagger documentation for webhooks.");
            return;
        }

        var path = "/webhook/{env}/{webhookId}";

        if (!swaggerDoc.Paths.ContainsKey(path))
            swaggerDoc.Paths[path] = new OpenApiPathItem();
            
        // Clear ALL operations before adding POST
        swaggerDoc.Paths[path].Operations.Clear();

        // Define the POST operation
        var operation = new OpenApiOperation
        {
            Tags = new List<OpenApiTag> { new() { Name = "Webhooks" } },
            Summary = "Process incoming webhook",
            Description = "Receives and stores webhook data in the database",
            OperationId = "process_webhook",
            Parameters = new List<OpenApiParameter>
            {
                new()
                {
                    Name = "env",
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = new OpenApiSchema
                    {
                        Type = "string",
                        Enum = environments.Select(e => new OpenApiString(e!)).Cast<IOpenApiAny>().ToList()
                    },
                    Description = "Target environment"
                },
                new()
                {
                    Name = "webhookId",
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = new OpenApiSchema
                    {
                        Type = "string"
                    },
                    Description = "Webhook identifier (secret)"
                }
            },
            RequestBody = new OpenApiRequestBody
            {
                Description = "Webhook payload (any valid JSON)",
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object",
                            AdditionalProperties = new OpenApiSchema { Type = "object" }
                        }
                    }
                }
            },
            Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse 
                { 
                    Description = "Webhook processed successfully",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = "object",
                                Properties = new Dictionary<string, OpenApiSchema>
                                {
                                    ["message"] = new OpenApiSchema { Type = "string" },
                                    ["id"] = new OpenApiSchema { Type = "integer", Format = "int32" }
                                }
                            }
                        }
                    }
                },
                ["400"] = new OpenApiResponse { Description = "Bad Request" },
                ["404"] = new OpenApiResponse { Description = "Webhook ID not found or not configured" },
                ["500"] = new OpenApiResponse { Description = "Server Error" }
            }
        };

        // Only add the POST operation, ensuring no GET operation exists
        swaggerDoc.Paths[path].Operations[OperationType.Post] = operation;
    }

    private static List<string> GetEnvironmentNames()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "environments");
        if (!Directory.Exists(root)) return new List<string> { "dev" };

        return Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();
    }
}
public static class SwaggerConfiguration
{
    public static SwaggerSettings ConfigureSwagger(WebApplicationBuilder builder)
    {
        var swaggerSettings = LoadSwaggerSettings(builder.Configuration);

        if (swaggerSettings.Enabled)
        {
            builder.Services.AddSwaggerGen(c =>
            {
                ConfigureSwaggerOptions(c, swaggerSettings);
                c.DocumentFilter<MinimalSqlReaderDocumentFilter>();
            });

            Log.Information("✅ Swagger services registered successfully");
        }
        else
        {
            Log.Information("ℹ️ Swagger is disabled in configuration");
        }

        return swaggerSettings;
    }

    private static SwaggerSettings LoadSwaggerSettings(IConfiguration configuration)
    {
        var swaggerSettings = new SwaggerSettings();

        try
        {
            var section = configuration.GetSection("Swagger");
            if (section.Exists())
            {
                section.Bind(swaggerSettings);
                Log.Information("✅ Swagger configuration loaded from appsettings.json");
            }
            else
            {
                Log.Warning("⚠️ No 'Swagger' section found in configuration. Using default settings.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error loading Swagger configuration. Using default settings.");
        }

        // Initialize default values if needed
        swaggerSettings.Contact ??= new ContactInfo();
        swaggerSettings.SecurityDefinition ??= new SecurityDefinitionInfo();

        if (string.IsNullOrWhiteSpace(swaggerSettings.Title))
            swaggerSettings.Title = "API";

        if (string.IsNullOrWhiteSpace(swaggerSettings.Version))
            swaggerSettings.Version = "v1";

        if (string.IsNullOrWhiteSpace(swaggerSettings.SecurityDefinition.Name))
            swaggerSettings.SecurityDefinition.Name = "Bearer";

        if (string.IsNullOrWhiteSpace(swaggerSettings.SecurityDefinition.Scheme))
            swaggerSettings.SecurityDefinition.Scheme = "Bearer";

        return swaggerSettings;
    }

    private static void ConfigureSwaggerOptions(SwaggerGenOptions options, SwaggerSettings settings)
    {
        options.SwaggerDoc(settings.Version, new OpenApiInfo
        {
            Title = settings.Title,
            Version = settings.Version,
            Description = settings.Description ?? "API Documentation",
            Contact = new OpenApiContact
            {
                Name = settings.Contact.Name,
                Email = settings.Contact.Email
            }
        });

        options.AddSecurityDefinition(settings.SecurityDefinition.Name, new OpenApiSecurityScheme
        {
            Description = settings.SecurityDefinition.Description,
            Name = "Authorization",
            In = Enum.TryParse<ParameterLocation>(settings.SecurityDefinition.In, true, out var paramLocation) 
                ? paramLocation 
                : ParameterLocation.Header,
            Type = Enum.TryParse<SecuritySchemeType>(settings.SecurityDefinition.Type, true, out var type) 
                ? type 
                : SecuritySchemeType.ApiKey,
            Scheme = settings.SecurityDefinition.Scheme
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = settings.SecurityDefinition.Name
                    }
                },
                new string[] { }
            }
        });
    }
}

public class SwaggerSettings
{
    public bool Enabled { get; set; } = true;
    public string Title { get; set; } = "API";
    public string Version { get; set; } = "v1";
    public string Description { get; set; } = "Documentation";
    public ContactInfo Contact { get; set; } = new ContactInfo();
    public SecurityDefinitionInfo SecurityDefinition { get; set; } = new SecurityDefinitionInfo();
    public string RoutePrefix { get; set; } = "swagger";
    public string DocExpansion { get; set; } = "List";
    public int DefaultModelsExpandDepth { get; set; } = -1;
    public bool DisplayRequestDuration { get; set; } = true;
    public bool EnableFilter { get; set; } = true;
    public bool EnableDeepLinking { get; set; } = true;
    public bool EnableValidator { get; set; } = true;
}

public class ContactInfo
{
    public string Name { get; set; } = "Support";
    public string Email { get; set; } = string.Empty;
}

public class SecurityDefinitionInfo
{
    public string Name { get; set; } = "Bearer";
    public string Description { get; set; } = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"";
    public string In { get; set; } = "Header";
    public string Type { get; set; } = "ApiKey";
    public string Scheme { get; set; } = "Bearer";
}