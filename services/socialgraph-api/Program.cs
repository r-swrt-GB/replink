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

    // User trains at Club
    app.MapPost("/api/graph/trains-at/{clubId:guid}", async (Guid clubId, HttpContext context, IDriver driver) =>
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
                MERGE (u:User {id: $userId})
                MERGE (c:Club {id: $clubId})
                MERGE (u)-[:TRAINS_AT]->(c)
            ";
            await tx.RunAsync(query, new { userId, clubId = clubId.ToString() });
        });

        Log.Information("User {UserId} now trains at club {ClubId}", userId, clubId);

        return Results.Ok(new { message = "Training relationship created" });
    }).RequireAuthorization();

    // User stops training at Club
    app.MapDelete("/api/graph/trains-at/{clubId:guid}", async (Guid clubId, HttpContext context, IDriver driver) =>
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
                MATCH (u:User {id: $userId})-[r:TRAINS_AT]->(c:Club {id: $clubId})
                DELETE r
            ";
            await tx.RunAsync(query, new { userId, clubId = clubId.ToString() });
        });

        Log.Information("User {UserId} stopped training at club {ClubId}", userId, clubId);

        return Results.Ok(new { message = "Training relationship removed" });
    }).RequireAuthorization();

    // Get clubs where user trains
    app.MapGet("/api/graph/user/{userId:guid}/clubs", async (Guid userId, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();
        var clubs = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (u:User {id: $userId})-[:TRAINS_AT]->(c:Club)
                RETURN c.id AS id
            ";
            var cursor = await tx.RunAsync(query, new { userId = userId.ToString() });
            var records = await cursor.ToListAsync();
            return records.Select(r => r["id"].As<string>()).ToList();
        });

        return Results.Ok(new { userId, clubs });
    }).RequireAuthorization();

    // Create workout performance relationship
    app.MapPost("/api/graph/performs/{workoutId:guid}", async (Guid workoutId, HttpContext context, IDriver driver) =>
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
                MERGE (u:User {id: $userId})
                MERGE (w:Workout {id: $workoutId})
                MERGE (u)-[:PERFORMS]->(w)
            ";
            await tx.RunAsync(query, new { userId, workoutId = workoutId.ToString() });
        });

        Log.Information("User {UserId} performs workout {WorkoutId}", userId, workoutId);

        return Results.Ok(new { message = "Workout performance relationship created" });
    }).RequireAuthorization();

    // Link workout to exercises
    app.MapPost("/api/graph/workout/{workoutId:guid}/exercises", async (Guid workoutId, LinkExercisesRequest request, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            var query = @"
                MERGE (w:Workout {id: $workoutId})
                WITH w
                UNWIND $exerciseIds AS exerciseId
                MERGE (e:Exercise {id: exerciseId})
                MERGE (w)-[:CONTAINS]->(e)
            ";
            await tx.RunAsync(query, new { workoutId = workoutId.ToString(), exerciseIds = request.ExerciseIds.Select(e => e.ToString()).ToList() });
        });

        Log.Information("Workout {WorkoutId} linked to {Count} exercises", workoutId, request.ExerciseIds.Length);

        return Results.Ok(new { message = "Exercises linked to workout" });
    }).RequireAuthorization();

    // Get exercises in a workout
    app.MapGet("/api/graph/workout/{workoutId:guid}/exercises", async (Guid workoutId, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();
        var exercises = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (w:Workout {id: $workoutId})-[:CONTAINS]->(e:Exercise)
                RETURN e.id AS id
            ";
            var cursor = await tx.RunAsync(query, new { workoutId = workoutId.ToString() });
            var records = await cursor.ToListAsync();
            return records.Select(r => r["id"].As<string>()).ToList();
        });

        return Results.Ok(new { workoutId, exercises });
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

public record LinkExercisesRequest(Guid[] ExerciseIds);
