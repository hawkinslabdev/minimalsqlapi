using DynamicODataToSQL;
using DynamicODataToSQL.Interfaces;
using MinimalSqlReader.Classes;
using MinimalSqlReader.Interfaces;
using MinimalSqlReader.Swagger;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Text.Json;
using SqlKata.Compilers;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: false)
                     .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration) 
    .WriteTo.Console() 
    .WriteTo.File(
        path: "log/minimalsqlreader-.log",
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 10,
        buffered: true,
        flushToDiskInterval: TimeSpan.FromSeconds(30))
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

Log.Information($"🔧 Current environment: {builder.Environment.EnvironmentName}");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHostedService<LogFlusher>();

// 🧩 Register required services for DynamicODataToSQL
builder.Services.AddSingleton<IHostedService, StartupLogger>();
builder.Services.AddSingleton<IEdmModelBuilder, EdmModelBuilder>();
builder.Services.AddSingleton<Compiler, SqlServerCompiler>();
builder.Services.AddSingleton<IODataToSqlConverter, ODataToSqlConverter>();
builder.Services.AddSingleton<IEnvironmentSettingsProvider, EnvironmentSettingsProvider>();
builder.Services.AddScoped<TokenService>();

var swaggerSettings = SwaggerConfiguration.ConfigureSwagger(builder);

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
    var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();
    
    try
    {
        context.Database.EnsureCreated();
        context.EnsureTablesCreated();

        if (!context.Tokens.Any())
        {
            string username = Environment.MachineName;
            var token = await tokenService.GenerateTokenAsync(username);
            Log.Information("🗝️ Generated token for {Username}: {Token}", username, token);
            Log.Information("💾 Token saved to: {Path}", Path.Combine(Directory.GetCurrentDirectory(), "tokens", $"{username}.txt"));
        }
    }
    catch (Exception ex)
    {
        Log.Error("❌ Authentication system initialization failed: {Message}", ex.Message);
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
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    response.ContentType = "application/json";

    string message = response.StatusCode switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        406 => "Not Acceptable",
        415 => "Unsupported Media Type",
        422 => "Unprocessable Entity",
        500 => "Internal Server Error",
        _   => "Unexpected Error"
    };

    Log.Warning("HTTP {StatusCode}: {Message} - {Method} {Path}", 
        response.StatusCode, message, context.HttpContext.Request.Method, context.HttpContext.Request.Path);

    var json = JsonSerializer.Serialize(new { error = message, success = false, statusCode = response.StatusCode });
    await response.WriteAsync(json);
});
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.Remove("X-Powered-By");
        context.Response.Headers.Remove("X-AspNet-Version");
        context.Response.Headers.Remove("X-AspNetMvc-Version");
        context.Response.Headers.Remove("Server");
        return Task.CompletedTask;
    });
    await next();
});

app.UseMiddleware<RateLimiter>();
app.UseAuthorization();
app.MapControllers();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var urls = app.Urls.Any() ? string.Join(", ", app.Urls) : "http://localhost:5252";
    Log.Information("🌐 Application running at: {Urls}", urls);
});

app.Lifetime.ApplicationStopping.Register(() => 
{
    Log.Information("Application shutting down...");
    Log.CloseAndFlush();
});

app.Run();
