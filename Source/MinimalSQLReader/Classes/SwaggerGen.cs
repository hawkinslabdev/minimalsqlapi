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
        var endpoints = EndpointHelper.GetEndpoints(silent: true);
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

        // Re-add only our custom dynamic endpoints
        foreach (var (endpointName, entity) in endpoints)
        {
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
        var swaggerSettings = new SwaggerSettings();

        try
        {
            var section = builder.Configuration.GetSection("Swagger");
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

        swaggerSettings.Contact ??= new ContactInfo();
        swaggerSettings.SecurityDefinition ??= new SecurityDefinitionInfo();

        if (string.IsNullOrWhiteSpace(swaggerSettings.Title))
            swaggerSettings.Title = "MinimalSqlReader API";

        if (string.IsNullOrWhiteSpace(swaggerSettings.Version))
            swaggerSettings.Version = "v1";

        if (string.IsNullOrWhiteSpace(swaggerSettings.SecurityDefinition.Name))
            swaggerSettings.SecurityDefinition.Name = "Bearer";

        if (string.IsNullOrWhiteSpace(swaggerSettings.SecurityDefinition.Scheme))
            swaggerSettings.SecurityDefinition.Scheme = "Bearer";

        if (swaggerSettings.Enabled)
        {
            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc(swaggerSettings.Version, new OpenApiInfo
                {
                    Title = swaggerSettings.Title,
                    Version = swaggerSettings.Version,
                    Description = swaggerSettings.Description ?? "API Documentation",
                    Contact = new OpenApiContact
                    {
                        Name = swaggerSettings.Contact.Name,
                        Email = swaggerSettings.Contact.Email
                    }
                });

                c.AddSecurityDefinition(swaggerSettings.SecurityDefinition.Name, new OpenApiSecurityScheme
                {
                    Description = swaggerSettings.SecurityDefinition.Description,
                    Name = "Authorization",
                    In = Enum.TryParse<ParameterLocation>(swaggerSettings.SecurityDefinition.In, true, out var paramLocation) ? paramLocation : ParameterLocation.Header,
                    Type = Enum.TryParse<SecuritySchemeType>(swaggerSettings.SecurityDefinition.Type, true, out var type) ? type : SecuritySchemeType.ApiKey,
                    Scheme = swaggerSettings.SecurityDefinition.Scheme
                });

                c.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = swaggerSettings.SecurityDefinition.Name
                            }
                        },
                        new string[] { }
                    }
                });

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