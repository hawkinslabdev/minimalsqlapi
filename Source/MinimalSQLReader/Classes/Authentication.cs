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

        // Verify token using the token service instead of direct DB query
        bool isValid = await tokenService.VerifyTokenAsync(token);
        if (!isValid)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            Log.Warning("❌ Invalid token");

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
        
        // Ensure tokens directory exists
        if (!Directory.Exists(_tokenFolderPath))
        {
            Directory.CreateDirectory(_tokenFolderPath);
        }
    }

    // Generate a new token for a user
    public async Task<string> GenerateTokenAsync(string username)
    {
        // Generate a random token
        string token = Guid.NewGuid().ToString();
        
        // Generate salt for hashing
        byte[] salt = GenerateSalt();
        string saltString = Convert.ToBase64String(salt);
        
        // Hash the token
        string hashedToken = HashToken(token, salt);
        
        // Create a new token entry
        var tokenEntry = new AuthToken
        {
            Username = username,
            TokenHash = hashedToken,
            TokenSalt = saltString,
            CreatedAt = DateTime.UtcNow
        };
        
        // Add to database
        _dbContext.Tokens.Add(tokenEntry);
        await _dbContext.SaveChangesAsync();
        
        // Save token to file
        await SaveTokenToFileAsync(username, token);
        
        return token;
    }

    // Verify if a token is valid
    public async Task<bool> VerifyTokenAsync(string token)
    {
        // Get all tokens
        var tokens = await _dbContext.Tokens.ToListAsync();
        
        // Check each token
        foreach (var storedToken in tokens)
        {
            // Convert stored salt from string to bytes
            byte[] salt = Convert.FromBase64String(storedToken.TokenSalt);
            
            // Hash the provided token with the stored salt
            string hashedToken = HashToken(token, salt);
            
            // Compare hashed tokens
            if (hashedToken == storedToken.TokenHash)
            {
                return true;
            }
        }
        
        return false;
    }

    // Helper method to hash a token
    private string HashToken(string token, byte[] salt)
    {
        using (var pbkdf2 = new Rfc2898DeriveBytes(token, salt, 10000, HashAlgorithmName.SHA256))
        {
            byte[] hash = pbkdf2.GetBytes(32);
            return Convert.ToBase64String(hash);
        }
    }

    // Helper method to generate a random salt
    private byte[] GenerateSalt()
    {
        byte[] salt = new byte[16];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(salt);
        }
        return salt;
    }

    // Helper method to save a token to a file
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