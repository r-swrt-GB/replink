using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Text;
using System.Security.Claims;
using Neo4j.Driver;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // Neo4j Configuration (reuse existing connection from social-graph)
    var neo4jUri = builder.Configuration["Neo4j:Uri"] ?? "bolt://neo4j:7687";
    var neo4jUser = builder.Configuration["Neo4j:User"] ?? "neo4j";
    var neo4jPassword = builder.Configuration["Neo4j:Password"] ?? "replinkneo4j";

    builder.Services.AddSingleton(GraphDatabase.Driver(neo4jUri, AuthTokens.Basic(neo4jUser, neo4jPassword)));

    // JWT Configuration
    var jwtSecret = builder.Configuration["JwtSettings:Secret"] ?? "your-super-secret-jwt-key-change-this-in-production";
    var jwtIssuer = builder.Configuration["JwtSettings:Issuer"] ?? "RepLink";
    var jwtAudience = builder.Configuration["JwtSettings:Audience"] ?? "RepLink";

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtIssuer,
                ValidAudience = jwtAudience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
            };
        });

    builder.Services.AddAuthorization();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddHealthChecks();

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    // Create Neo4j constraints on startup
    using (var scope = app.Services.CreateScope())
    {
        var driver = scope.ServiceProvider.GetRequiredService<IDriver>();
        await using var session = driver.AsyncSession();
        try
        {
            await session.ExecuteWriteAsync(async tx =>
            {
                // Create unique constraint on UserProfile.userId
                await tx.RunAsync("CREATE CONSTRAINT userprofile_userid_unique IF NOT EXISTS FOR (up:UserProfile) REQUIRE up.userId IS UNIQUE");

                // Create index on UserProfile.displayName for faster searches
                await tx.RunAsync("CREATE INDEX userprofile_displayname_index IF NOT EXISTS FOR (up:UserProfile) ON (up.displayName)");
            });
            Log.Information("Neo4j constraints and indexes created for UserProfiles");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create Neo4j constraints (may already exist)");
        }
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseAuthentication();
    app.UseAuthorization();

    // Health endpoint with Neo4j connectivity check
    app.MapGet("/api/users/health", async (IDriver driver) =>
    {
        try
        {
            await using var session = driver.AsyncSession();
            await session.ExecuteReadAsync(async tx =>
            {
                await tx.RunAsync("RETURN 1");
            });

            return Results.Ok(new
            {
                status = "Healthy",
                service = "Users API",
                database = "Neo4j",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Health check failed for Users API");
            return Results.Problem(new
            {
                status = "Unhealthy",
                service = "Users API",
                error = ex.Message,
                timestamp = DateTime.UtcNow
            }.ToString());
        }
    });

    // Get current user profile
    app.MapGet("/api/users/profile", async (HttpContext context, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        await using var session = driver.AsyncSession();
        var profile = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (up:UserProfile {userId: $userId})
                RETURN up.id AS id,
                       up.userId AS userId,
                       up.displayName AS displayName,
                       up.bio AS bio,
                       up.avatarUrl AS avatarUrl,
                       up.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, new { userId });
            var records = await cursor.ToListAsync();

            if (records.Count == 0)
                return null;

            var record = records[0];
            return new UserProfile
            {
                Id = record["id"].As<string>(),
                UserId = record["userId"].As<string>(),
                DisplayName = record["displayName"].As<string>(),
                Bio = record["bio"].As<string>(),
                AvatarUrl = record["avatarUrl"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            };
        });

        if (profile == null)
        {
            return Results.NotFound(new { error = "Profile not found" });
        }

        return Results.Ok(profile);
    }).RequireAuthorization();

    // Create/Update user profile
    app.MapPost("/api/users/profile", async (HttpContext context, UserProfileRequest request, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        await using var session = driver.AsyncSession();
        var profile = await session.ExecuteWriteAsync(async tx =>
        {
            // Use MERGE to create or update profile
            var query = @"
                MERGE (up:UserProfile {userId: $userId})
                ON CREATE SET
                    up.id = $id,
                    up.displayName = $displayName,
                    up.bio = $bio,
                    up.avatarUrl = $avatarUrl,
                    up.createdAt = datetime($createdAt)
                ON MATCH SET
                    up.displayName = $displayName,
                    up.bio = COALESCE($bio, up.bio),
                    up.avatarUrl = COALESCE($avatarUrl, up.avatarUrl)
                RETURN up.id AS id,
                       up.userId AS userId,
                       up.displayName AS displayName,
                       up.bio AS bio,
                       up.avatarUrl AS avatarUrl,
                       up.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, new
            {
                id = Guid.NewGuid().ToString(),
                userId,
                displayName = request.DisplayName,
                bio = request.Bio ?? string.Empty,
                avatarUrl = request.AvatarUrl ?? string.Empty,
                createdAt = DateTime.UtcNow.ToString("o")
            });

            var record = await cursor.SingleAsync();

            return new UserProfile
            {
                Id = record["id"].As<string>(),
                UserId = record["userId"].As<string>(),
                DisplayName = record["displayName"].As<string>(),
                Bio = record["bio"].As<string>(),
                AvatarUrl = record["avatarUrl"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            };
        });

        Log.Information("Created/Updated profile for user {UserId}", userId);

        return Results.Ok(profile);
    }).RequireAuthorization();

    // Search users
    app.MapGet("/api/users/search", async (string q, IDriver driver) =>
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.BadRequest(new { error = "Query parameter 'q' is required" });
        }

        await using var session = driver.AsyncSession();
        var profiles = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (up:UserProfile)
                WHERE toLower(up.displayName) CONTAINS toLower($query)
                RETURN up.id AS id,
                       up.userId AS userId,
                       up.displayName AS displayName,
                       up.bio AS bio,
                       up.avatarUrl AS avatarUrl,
                       up.createdAt AS createdAt
                ORDER BY up.displayName
                LIMIT 20
            ";

            var cursor = await tx.RunAsync(query, new { query = q });
            var records = await cursor.ToListAsync();

            return records.Select(record => new UserProfile
            {
                Id = record["id"].As<string>(),
                UserId = record["userId"].As<string>(),
                DisplayName = record["displayName"].As<string>(),
                Bio = record["bio"].As<string>(),
                AvatarUrl = record["avatarUrl"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            }).ToList();
        });

        return Results.Ok(profiles);
    }).RequireAuthorization();

    Log.Information("Users API (Neo4j) starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Users API failed to start");
}
finally
{
    Log.CloseAndFlush();
}

// Models
public class UserProfile
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public record UserProfileRequest(string DisplayName, string? Bio, string? AvatarUrl);
