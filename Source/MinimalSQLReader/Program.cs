using DynamicODataToSQL;
using DynamicODataToSQL.Interfaces;
using MinimalSqlReader.Classes;
using MinimalSqlReader.Swagger;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Text.Json;
using SqlKata.Compilers;

var builder = WebApplication.CreateBuilder(args);
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration) // Add this
    .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
    .WriteTo.File(
        path: "log/minimalsqlreader-.log",
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 5,
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
        buffered: true)
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Filter.ByExcluding(logEvent =>
        logEvent.Properties.ContainsKey("RequestPath") &&
        (logEvent.Properties["RequestPath"].ToString().Contains("/swagger") ||
         logEvent.Properties["RequestPath"].ToString().Contains("/index.html")))
    .CreateLogger();


builder.Host.UseSerilog();
builder.Configuration.AddJsonFile("appsettings.json", optional: false);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 🧩 Register required services for DynamicODataToSQL
builder.Services.AddSingleton<IHostedService, StartupLogger>();
builder.Services.AddSingleton<IEdmModelBuilder, EdmModelBuilder>();
builder.Services.AddSingleton<Compiler, SqlServerCompiler>();
builder.Services.AddSingleton<IODataToSqlConverter, ODataToSqlConverter>();
builder.Services.AddSingleton<EnvironmentSettings>();

var swaggerSettings = SwaggerConfiguration.ConfigureSwagger(builder);

// Configure SQLite Authentication Database
var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "auth.db");
builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseSqlite("Data Source=auth.db"));

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseDefaultFiles(new DefaultFilesOptions
{
    DefaultFileNames = new List<string> { "index.html" }
});
app.UseStaticFiles();

// Configure the authentication routes
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    try
    {
        context.Database.EnsureCreated();
        context.EnsureTablesCreated();

        if (!context.Tokens.Any())
        {
            var token = new AuthToken { Token = Guid.NewGuid().ToString() };
            context.Tokens.Add(token);
            context.SaveChanges();
            Log.Information("🗝️ Generated token: {Token}", token.Token);
        }
    }
    catch (Exception ex)
    {
        Log.Error("❌ Database initialization failed: {Message}", ex.Message);
    }
}

if (swaggerSettings.Enabled)
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", $"{swaggerSettings.Title} {swaggerSettings.Version}");
        c.RoutePrefix = swaggerSettings.RoutePrefix ?? "swagger";
        c.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
        c.DefaultModelsExpandDepth(swaggerSettings.DefaultModelsExpandDepth);
        if (swaggerSettings.DisplayRequestDuration) c.DisplayRequestDuration();
        if (swaggerSettings.EnableFilter) c.EnableFilter();
        if (swaggerSettings.EnableDeepLinking) c.EnableDeepLinking();
        if (swaggerSettings.EnableValidator) c.EnableValidator();
    });
}
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        var errorResponse = new { error = "Unexpected server error", detail = ex.Message };
        await context.Response.WriteAsync(JsonSerializer.Serialize(errorResponse));
    }
});

app.UseMiddleware<TokenAuthMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.Run();
