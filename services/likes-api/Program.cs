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
                // Create unique constraint on Like combination of postId and userId
                await tx.RunAsync("CREATE CONSTRAINT like_post_user_unique IF NOT EXISTS FOR (l:Like) REQUIRE (l.postId, l.userId) IS UNIQUE");

                // Create index on Like.postId for faster queries
                await tx.RunAsync("CREATE INDEX like_postid_index IF NOT EXISTS FOR (l:Like) ON (l.postId)");

                // Create index on Like.userId
                await tx.RunAsync("CREATE INDEX like_userid_index IF NOT EXISTS FOR (l:Like) ON (l.userId)");
            });
            Log.Information("Neo4j constraints and indexes created for Likes");
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

    // Health endpoint
    app.MapGet("/api/health", () => Results.Ok(new { status = "Healthy", service = "Likes API (Neo4j)" }));

    // Like a post
    app.MapPost("/api/posts/{postId}/likes", async (string postId, HttpContext context, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        await using var session = driver.AsyncSession();

        // Check if already liked
        var alreadyLiked = await session.ExecuteReadAsync(async tx =>
        {
            var query = "MATCH (l:Like {postId: $postId, userId: $userId}) RETURN l LIMIT 1";
            var cursor = await tx.RunAsync(query, new { postId, userId });
            return await cursor.FetchAsync();
        });

        if (alreadyLiked)
        {
            return Results.BadRequest(new { error = "Post already liked" });
        }

        var like = await session.ExecuteWriteAsync(async tx =>
        {
            var likeId = Guid.NewGuid().ToString();
            var createdAt = DateTime.UtcNow;

            var query = @"
                CREATE (l:Like {
                    id: $id,
                    postId: $postId,
                    userId: $userId,
                    createdAt: datetime($createdAt)
                })
                RETURN l.id AS id,
                       l.postId AS postId,
                       l.userId AS userId,
                       l.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, new
            {
                id = likeId,
                postId,
                userId,
                createdAt = createdAt.ToString("o")
            });

            var record = await cursor.SingleAsync();

            return new Like
            {
                Id = record["id"].As<string>(),
                PostId = record["postId"].As<string>(),
                UserId = record["userId"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            };
        });

        Log.Information("Post {PostId} liked by user {UserId}", postId, userId);

        return Results.Ok(like);
    }).RequireAuthorization();

    // Unlike a post
    app.MapDelete("/api/posts/{postId}/likes", async (string postId, HttpContext context, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        await using var session = driver.AsyncSession();
        var deleted = await session.ExecuteWriteAsync(async tx =>
        {
            var query = @"
                MATCH (l:Like {postId: $postId, userId: $userId})
                DELETE l
                RETURN count(l) AS deleted
            ";

            var cursor = await tx.RunAsync(query, new { postId, userId });
            var record = await cursor.SingleAsync();
            return record["deleted"].As<int>() > 0;
        });

        if (!deleted)
        {
            return Results.NotFound(new { error = "Like not found" });
        }

        Log.Information("Post {PostId} unliked by user {UserId}", postId, userId);

        return Results.Ok(new { message = "Like removed" });
    }).RequireAuthorization();

    // Get like count for post
    app.MapGet("/api/posts/{postId}/likes", async (string postId, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();
        var count = await session.ExecuteReadAsync(async tx =>
        {
            var query = "MATCH (l:Like {postId: $postId}) RETURN count(l) AS count";
            var cursor = await tx.RunAsync(query, new { postId });
            var record = await cursor.SingleAsync();
            return record["count"].As<int>();
        });

        return Results.Ok(new { postId, likesCount = count });
    }).RequireAuthorization();

    // Check if user liked a post
    app.MapGet("/api/posts/{postId}/likes/me", async (string postId, HttpContext context, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        await using var session = driver.AsyncSession();
        var hasLiked = await session.ExecuteReadAsync(async tx =>
        {
            var query = "MATCH (l:Like {postId: $postId, userId: $userId}) RETURN l LIMIT 1";
            var cursor = await tx.RunAsync(query, new { postId, userId });
            return await cursor.FetchAsync();
        });

        return Results.Ok(new { postId, hasLiked });
    }).RequireAuthorization();

    Log.Information("Likes API (Neo4j) starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Likes API failed to start");
}
finally
{
    Log.CloseAndFlush();
}

// Models
public class Like
{
    public string Id { get; set; } = string.Empty;
    public string PostId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
