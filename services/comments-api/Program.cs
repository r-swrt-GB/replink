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
                // Create unique constraint on Comment.id
                await tx.RunAsync("CREATE CONSTRAINT comment_id_unique IF NOT EXISTS FOR (c:Comment) REQUIRE c.id IS UNIQUE");

                // Create index on Comment.postId for faster queries
                await tx.RunAsync("CREATE INDEX comment_postid_index IF NOT EXISTS FOR (c:Comment) ON (c.postId)");

                // Create index on Comment.userId
                await tx.RunAsync("CREATE INDEX comment_userid_index IF NOT EXISTS FOR (c:Comment) ON (c.userId)");

                // Create index on Comment.createdAt for ordering
                await tx.RunAsync("CREATE INDEX comment_createdat_index IF NOT EXISTS FOR (c:Comment) ON (c.createdAt)");
            });
            Log.Information("Neo4j constraints and indexes created for Comments");
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
    app.MapGet("/api/health", () => Results.Ok(new { status = "Healthy", service = "Comments API (Neo4j)" }));

    // Create comment
    app.MapPost("/api/comments", async (HttpContext context, CreateCommentRequest request, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var commentId = Guid.NewGuid().ToString();
        var createdAt = DateTime.UtcNow;

        await using var session = driver.AsyncSession();
        var comment = await session.ExecuteWriteAsync(async tx =>
        {
            var query = @"
                CREATE (c:Comment {
                    id: $id,
                    postId: $postId,
                    userId: $userId,
                    content: $content,
                    createdAt: datetime($createdAt)
                })
                RETURN c.id AS id,
                       c.postId AS postId,
                       c.userId AS userId,
                       c.content AS content,
                       c.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, new
            {
                id = commentId,
                postId = request.PostId.ToString(),
                userId,
                content = request.Content,
                createdAt = createdAt.ToString("o")
            });

            var record = await cursor.SingleAsync();

            return new Comment
            {
                Id = record["id"].As<string>(),
                PostId = record["postId"].As<string>(),
                UserId = record["userId"].As<string>(),
                Content = record["content"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            };
        });

        Log.Information("Comment created: {CommentId} on post {PostId} by user {UserId}", commentId, request.PostId, userId);

        return Results.Ok(comment);
    }).RequireAuthorization();

    // Get comments for post
    app.MapGet("/api/posts/{postId}/comments", async (string postId, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();
        var comments = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (c:Comment {postId: $postId})
                RETURN c.id AS id,
                       c.postId AS postId,
                       c.userId AS userId,
                       c.content AS content,
                       c.createdAt AS createdAt
                ORDER BY c.createdAt DESC
            ";

            var cursor = await tx.RunAsync(query, new { postId });
            var records = await cursor.ToListAsync();

            return records.Select(record => new Comment
            {
                Id = record["id"].As<string>(),
                PostId = record["postId"].As<string>(),
                UserId = record["userId"].As<string>(),
                Content = record["content"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            }).ToList();
        });

        return Results.Ok(comments);
    }).RequireAuthorization();

    // Delete comment
    app.MapDelete("/api/comments/{commentId}", async (string commentId, HttpContext context, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        await using var session = driver.AsyncSession();
        var deleted = await session.ExecuteWriteAsync(async tx =>
        {
            // First check if comment exists and belongs to user
            var checkQuery = "MATCH (c:Comment {id: $commentId, userId: $userId}) RETURN c";
            var checkCursor = await tx.RunAsync(checkQuery, new { commentId, userId });
            var exists = await checkCursor.FetchAsync();

            if (!exists)
                return false;

            // Delete the comment
            var deleteQuery = @"
                MATCH (c:Comment {id: $commentId, userId: $userId})
                DELETE c
                RETURN count(c) AS deleted
            ";

            var cursor = await tx.RunAsync(deleteQuery, new { commentId, userId });
            var record = await cursor.SingleAsync();
            return record["deleted"].As<int>() > 0;
        });

        if (!deleted)
        {
            return Results.NotFound(new { error = "Comment not found or unauthorized" });
        }

        Log.Information("Comment {CommentId} deleted by user {UserId}", commentId, userId);

        return Results.Ok(new { message = "Comment deleted" });
    }).RequireAuthorization();

    Log.Information("Comments API (Neo4j) starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Comments API failed to start");
}
finally
{
    Log.CloseAndFlush();
}

// Models
public class Comment
{
    public string Id { get; set; } = string.Empty;
    public string PostId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public record CreateCommentRequest(Guid PostId, string Content);
