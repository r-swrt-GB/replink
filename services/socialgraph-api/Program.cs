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

    // Neo4j Configuration
    var neo4jUri = builder.Configuration["Neo4j:Uri"] ?? "bolt://localhost:7687";
    var neo4jUser = builder.Configuration["Neo4j:User"] ?? "neo4j";
    var neo4jPassword = builder.Configuration["Neo4j:Password"] ?? "password";

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

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseAuthentication();
    app.UseAuthorization();

    // Health endpoint
    app.MapGet("/api/graph/health", () => Results.Ok(new { status = "Healthy", service = "SocialGraph API" }));

    // Follow user
    app.MapPost("/api/graph/follow/{targetUserId:guid}", async (Guid targetUserId, HttpContext context, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        await using var session = driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            var query = @"
                MERGE (a:User {id: $userId})
                MERGE (b:User {id: $targetUserId})
                MERGE (a)-[:FOLLOWS]->(b)
            ";
            await tx.RunAsync(query, new { userId, targetUserId = targetUserId.ToString() });
        });

        Log.Information("User {UserId} followed {TargetUserId}", userId, targetUserId);

        return Results.Ok(new { message = "Followed successfully" });
    }).RequireAuthorization();

    // Unfollow user
    app.MapPost("/api/graph/unfollow/{targetUserId:guid}", async (Guid targetUserId, HttpContext context, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        await using var session = driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            var query = @"
                MATCH (a:User {id: $userId})-[r:FOLLOWS]->(b:User {id: $targetUserId})
                DELETE r
            ";
            await tx.RunAsync(query, new { userId, targetUserId = targetUserId.ToString() });
        });

        Log.Information("User {UserId} unfollowed {TargetUserId}", userId, targetUserId);

        return Results.Ok(new { message = "Unfollowed successfully" });
    }).RequireAuthorization();

    // Get followers
    app.MapGet("/api/graph/followers/{userId:guid}", async (Guid userId, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();
        var followers = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (follower:User)-[:FOLLOWS]->(user:User {id: $userId})
                RETURN follower.id AS id
            ";
            var cursor = await tx.RunAsync(query, new { userId = userId.ToString() });
            var records = await cursor.ToListAsync();
            return records.Select(r => r["id"].As<string>()).ToList();
        });

        return Results.Ok(new { userId, followers });
    }).RequireAuthorization();

    // Get following
    app.MapGet("/api/graph/following/{userId:guid}", async (Guid userId, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();
        var following = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (user:User {id: $userId})-[:FOLLOWS]->(following:User)
                RETURN following.id AS id
            ";
            var cursor = await tx.RunAsync(query, new { userId = userId.ToString() });
            var records = await cursor.ToListAsync();
            return records.Select(r => r["id"].As<string>()).ToList();
        });

        return Results.Ok(new { userId, following });
    }).RequireAuthorization();

    // Get recommendations (users followed by people you follow, but not followed by you)
    app.MapGet("/api/graph/recommendations", async (HttpContext context, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        await using var session = driver.AsyncSession();
        var recommendations = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (me:User {id: $userId})-[:FOLLOWS]->(friend:User)-[:FOLLOWS]->(recommendation:User)
                WHERE NOT (me)-[:FOLLOWS]->(recommendation) AND recommendation.id <> $userId
                RETURN DISTINCT recommendation.id AS id
                LIMIT 10
            ";
            var cursor = await tx.RunAsync(query, new { userId });
            var records = await cursor.ToListAsync();
            return records.Select(r => r["id"].As<string>()).ToList();
        });

        return Results.Ok(new { recommendations });
    }).RequireAuthorization();

    // Get feed sources (IDs of users current user follows)
    app.MapGet("/api/graph/feed-sources", async (HttpContext context, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        await using var session = driver.AsyncSession();
        var feedSources = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (me:User {id: $userId})-[:FOLLOWS]->(following:User)
                RETURN following.id AS id
            ";
            var cursor = await tx.RunAsync(query, new { userId });
            var records = await cursor.ToListAsync();
            return records.Select(r => r["id"].As<string>()).ToList();
        });

        return Results.Ok(new { userIds = feedSources });
    }).RequireAuthorization();

    Log.Information("SocialGraph API starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "SocialGraph API failed to start");
}
finally
{
    Log.CloseAndFlush();
}
