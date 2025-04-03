using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace MinimalSqlReader.Classes;

public class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    public TokenAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AuthDbContext dbContext, TokenService tokenService)
    {
        var path = context.Request.Path.ToString().ToLower();

        // Skip authentication for Swagger and index
        if (path.StartsWith("/swagger") || path == "/index.html")
        {
            await _next(context);
            return;
        }

        // Check authorization header
        if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader) || !authHeader.ToString().StartsWith("Bearer "))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            Log.Warning("❌ Invalid authentication header received.");

            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = "Invalid authentication header",
                success = false
            }));
            return;
        }

        var token = authHeader.ToString().Substring("Bearer ".Length).Trim();

        // Validate token using the TokenService
        bool isValidToken = await tokenService.VerifyTokenAsync(token);
        if (!isValidToken)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            Log.Warning("❌ Invalid token: {TokenPrefix}", token.Substring(0, Math.Min(8, token.Length)));

            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = "Invalid token",
                success = false
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
                Username TEXT NOT NULL,
                TokenHash TEXT NOT NULL,
                TokenSalt TEXT NOT NULL,
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            )");
    }
}

public class AuthToken
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string TokenSalt { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TokenService
{
    private readonly AuthDbContext _dbContext;
    private readonly string _tokenFolderPath;

    public TokenService(AuthDbContext dbContext)
    {
        _dbContext = dbContext;
        _tokenFolderPath = Path.Combine(Directory.GetCurrentDirectory(), "tokens");
        
        
        if (!Directory.Exists(_tokenFolderPath))
        {
            Directory.CreateDirectory(_tokenFolderPath);
        }
    }
  
    public async Task<string> GenerateTokenAsync(string username)
    {
        string token = Guid.NewGuid().ToString();
        
        byte[] salt = GenerateSalt();
        string saltString = Convert.ToBase64String(salt);
        string hashedToken = HashToken(token, salt);
        
        
        var tokenEntry = new AuthToken
        {
            Username = username,
            TokenHash = hashedToken,
            TokenSalt = saltString,
            CreatedAt = DateTime.UtcNow
        };
        
        
        _dbContext.Tokens.Add(tokenEntry);
        await _dbContext.SaveChangesAsync();
        
        
        await SaveTokenToFileAsync(username, token);
        
        return token;
    }

    public async Task<bool> VerifyTokenAsync(string token)
    {
        
        var tokens = await _dbContext.Tokens.ToListAsync();
        
        
        foreach (var storedToken in tokens)
        {
            
            byte[] salt = Convert.FromBase64String(storedToken.TokenSalt);
            string hashedToken = HashToken(token, salt);
            
            if (hashedToken == storedToken.TokenHash)
            {
                return true;
            }
        }
        
        return false;
    }

    private string HashToken(string token, byte[] salt)
    {
        using (var pbkdf2 = new Rfc2898DeriveBytes(token, salt, 10000, HashAlgorithmName.SHA256))
        {
            byte[] hash = pbkdf2.GetBytes(32);
            return Convert.ToBase64String(hash);
        }
    }

    
    private byte[] GenerateSalt()
    {
        byte[] salt = new byte[16];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(salt);
        }
        return salt;
    }

    
    private async Task SaveTokenToFileAsync(string username, string token)
    {
        try
        {
            string filePath = Path.Combine(_tokenFolderPath, $"{username}.txt");
            await File.WriteAllTextAsync(filePath, token);
            Log.Information("🔑 Token file saved to {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Failed to save token file for user {Username}", username);
            throw;
        }
    }
}