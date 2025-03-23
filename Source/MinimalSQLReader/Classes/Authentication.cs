namespace MinimalSqlReader.Classes;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Serilog;

public class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    public TokenAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AuthDbContext dbContext)
    {
        var path = context.Request.Path.ToString().ToLower();

        if (path.StartsWith("/swagger") || path == "/index.html")
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader) || !authHeader.ToString().StartsWith("Bearer "))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            Log.Warning("❌ Invalid authentication header received.");

            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = "Invalid authentication header"
            }));
            return;
        }

        var token = authHeader.ToString().Substring("Bearer ".Length).Trim();

        var tokenExists = dbContext.Tokens.Any(t => t.Token == token);
        if (!tokenExists)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            Log.Warning("❌ Invalid token: {Token}", token);

            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = "Invalid token"
            }));
            return;
        }

        await _next(context);
    }
}
public class AuthDbContext : DbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }

    public DbSet<AuthToken> Tokens { get; set; }

    public void EnsureTablesCreated()
    {
        Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS Tokens (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Token TEXT NOT NULL UNIQUE,
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            )");
    }
}

public class AuthToken
{
    public int Id { get; set; }
    public required string Token { get; set; }
}

