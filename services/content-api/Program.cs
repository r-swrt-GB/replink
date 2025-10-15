using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Text;
using System.Security.Claims;
using System.Linq;
using System.Collections.Generic;
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
                // Post constraints and indexes
                await tx.RunAsync("CREATE CONSTRAINT post_id_unique IF NOT EXISTS FOR (p:Post) REQUIRE p.id IS UNIQUE");
                await tx.RunAsync("CREATE INDEX post_userid_index IF NOT EXISTS FOR (p:Post) ON (p.userId)");
                await tx.RunAsync("CREATE INDEX post_createdat_index IF NOT EXISTS FOR (p:Post) ON (p.createdAt)");

                // Comment constraints and indexes
                await tx.RunAsync("CREATE CONSTRAINT comment_id_unique IF NOT EXISTS FOR (c:Comment) REQUIRE c.id IS UNIQUE");
                await tx.RunAsync("CREATE INDEX comment_postid_index IF NOT EXISTS FOR (c:Comment) ON (c.postId)");
                await tx.RunAsync("CREATE INDEX comment_userid_index IF NOT EXISTS FOR (c:Comment) ON (c.userId)");
                await tx.RunAsync("CREATE INDEX comment_createdat_index IF NOT EXISTS FOR (c:Comment) ON (c.createdAt)");

                // Like constraints and indexes
                await tx.RunAsync("CREATE CONSTRAINT like_post_user_unique IF NOT EXISTS FOR (l:Like) REQUIRE (l.postId, l.userId) IS UNIQUE");
                await tx.RunAsync("CREATE INDEX like_postid_index IF NOT EXISTS FOR (l:Like) ON (l.postId)");
                await tx.RunAsync("CREATE INDEX like_userid_index IF NOT EXISTS FOR (l:Like) ON (l.userId)");
            });
            Log.Information("Neo4j constraints and indexes created for Content API (Posts, Comments, Likes)");
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
    app.MapGet("/api/content/health", async (IDriver driver) =>
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
                service = "Content API",
                database = "Neo4j",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Health check failed for Content API");
            return Results.Problem(new
            {
                status = "Unhealthy",
                service = "Content API",
                error = ex.Message,
                timestamp = DateTime.UtcNow
            }.ToString());
        }
    });

    // ========== POST ENDPOINTS ==========

    // Create post
    app.MapPost("/api/posts", async (HttpContext context, CreatePostRequest request, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var postId = Guid.NewGuid().ToString();
        var createdAt = DateTime.UtcNow;

        await using var session = driver.AsyncSession();
        var post = await session.ExecuteWriteAsync(async tx =>
        {
            var query = @"
                CREATE (p:Post {
                    id: $id,
                    userId: $userId,
                    caption: $caption,
                    mediaUrl: $mediaUrl,
                    hashtags: $hashtags,
                    exerciseIds: $exerciseIds,
                    createdAt: datetime($createdAt),
                    commentsCount: 0
                })
                RETURN p.id AS id,
                       p.userId AS userId,
                       p.caption AS caption,
                       p.mediaUrl AS mediaUrl,
                       p.hashtags AS hashtags,
                       p.exerciseIds AS exerciseIds,
                       p.createdAt AS createdAt,
                       p.commentsCount AS commentsCount
            ";

            var cursor = await tx.RunAsync(query, new
            {
                id = postId,
                userId,
                caption = request.Caption ?? string.Empty,
                mediaUrl = request.MediaUrl ?? string.Empty,
                hashtags = request.Hashtags ?? Array.Empty<string>(),
                exerciseIds = request.ExerciseIds?.Select(e => e.ToString()).ToArray() ?? Array.Empty<string>(),
                createdAt = createdAt.ToString("o")
            });

            var record = await cursor.SingleAsync();

            return new Post
            {
                Id = record["id"].As<string>(),
                UserId = record["userId"].As<string>(),
                Caption = record["caption"].As<string>(),
                MediaUrl = record["mediaUrl"].As<string>(),
                Hashtags = ConvertToStringArray(record["hashtags"]),
                ExerciseIds = ConvertToStringArray(record["exerciseIds"]),
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>()),
                CommentsCount = record["commentsCount"].As<int>(),
                LikesCount = 0
            };
        });

        Log.Information("Post created: {PostId} by user {UserId}", postId, userId);

        return Results.Ok(post);
    }).RequireAuthorization();

    // Get all posts (with likes count - NO HTTP CALLS!)
    app.MapGet("/api/posts", async (HttpContext context, IDriver driver, int? limit, int? offset) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        await using var session = driver.AsyncSession();
        var posts = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (p:Post)
                OPTIONAL MATCH (l:Like {postId: p.id})
                WITH p, count(l) AS likesCount,
                     CASE WHEN $userId IS NOT NULL THEN
                        EXISTS { MATCH (l2:Like {postId: p.id, userId: $userId}) }
                     ELSE false END AS hasLiked
                RETURN p.id AS id,
                       p.userId AS userId,
                       p.caption AS caption,
                       p.mediaUrl AS mediaUrl,
                       p.hashtags AS hashtags,
                       p.exerciseIds AS exerciseIds,
                       p.createdAt AS createdAt,
                       p.commentsCount AS commentsCount,
                       likesCount,
                       hasLiked
                ORDER BY p.createdAt DESC
                SKIP $offset
                LIMIT $limit
            ";

            var cursor = await tx.RunAsync(query, new {
                offset = offset ?? 0,
                limit = limit ?? 20,
                userId = userId
            });

            var records = await cursor.ToListAsync();
            return records.Select(record => new Post
            {
                Id = record["id"].As<string>(),
                UserId = record["userId"].As<string>(),
                Caption = record["caption"].As<string>(),
                MediaUrl = record["mediaUrl"].As<string>(),
                Hashtags = ConvertToStringArray(record["hashtags"]),
                ExerciseIds = ConvertToStringArray(record["exerciseIds"]),
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>()),
                CommentsCount = record["commentsCount"].As<int>(),
                LikesCount = record["likesCount"].As<int>(),
                HasLiked = record["hasLiked"].As<bool>()
            }).ToList();
        });

        return Results.Ok(posts);
    }).RequireAuthorization();

    // Get single post (with likes count)
    app.MapGet("/api/posts/{id}", async (string id, HttpContext context, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        await using var session = driver.AsyncSession();
        var post = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (p:Post {id: $id})
                OPTIONAL MATCH (l:Like {postId: p.id})
                WITH p, count(l) AS likesCount,
                     CASE WHEN $userId IS NOT NULL THEN
                        EXISTS { MATCH (l2:Like {postId: p.id, userId: $userId}) }
                     ELSE false END AS hasLiked
                RETURN p.id AS id,
                       p.userId AS userId,
                       p.caption AS caption,
                       p.mediaUrl AS mediaUrl,
                       p.hashtags AS hashtags,
                       p.exerciseIds AS exerciseIds,
                       p.createdAt AS createdAt,
                       p.commentsCount AS commentsCount,
                       likesCount,
                       hasLiked
            ";

            var cursor = await tx.RunAsync(query, new { id, userId });
            var records = await cursor.ToListAsync();

            if (records.Count == 0)
                return null;

            var record = records[0];
            return new Post
            {
                Id = record["id"].As<string>(),
                UserId = record["userId"].As<string>(),
                Caption = record["caption"].As<string>(),
                MediaUrl = record["mediaUrl"].As<string>(),
                Hashtags = ConvertToStringArray(record["hashtags"]),
                ExerciseIds = ConvertToStringArray(record["exerciseIds"]),
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>()),
                CommentsCount = record["commentsCount"].As<int>(),
                LikesCount = record["likesCount"].As<int>(),
                HasLiked = record["hasLiked"].As<bool>()
            };
        });

        if (post == null)
        {
            return Results.NotFound(new { error = "Post not found" });
        }

        return Results.Ok(post);
    }).RequireAuthorization();

    // Get user's posts (with likes count)
    app.MapGet("/api/posts/user/{userId}", async (string userId, HttpContext context, IDriver driver, int? limit, int? offset) =>
    {
        var currentUserId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        await using var session = driver.AsyncSession();
        var posts = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (p:Post {userId: $userId})
                OPTIONAL MATCH (l:Like {postId: p.id})
                WITH p, count(l) AS likesCount,
                     CASE WHEN $currentUserId IS NOT NULL THEN
                        EXISTS { MATCH (l2:Like {postId: p.id, userId: $currentUserId}) }
                     ELSE false END AS hasLiked
                RETURN p.id AS id,
                       p.userId AS userId,
                       p.caption AS caption,
                       p.mediaUrl AS mediaUrl,
                       p.hashtags AS hashtags,
                       p.exerciseIds AS exerciseIds,
                       p.createdAt AS createdAt,
                       p.commentsCount AS commentsCount,
                       likesCount,
                       hasLiked
                ORDER BY p.createdAt DESC
                SKIP $offset
                LIMIT $limit
            ";

            var cursor = await tx.RunAsync(query, new {
                userId,
                currentUserId,
                offset = offset ?? 0,
                limit = limit ?? 20
            });

            var records = await cursor.ToListAsync();
            return records.Select(record => new Post
            {
                Id = record["id"].As<string>(),
                UserId = record["userId"].As<string>(),
                Caption = record["caption"].As<string>(),
                MediaUrl = record["mediaUrl"].As<string>(),
                Hashtags = ConvertToStringArray(record["hashtags"]),
                ExerciseIds = ConvertToStringArray(record["exerciseIds"]),
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>()),
                CommentsCount = record["commentsCount"].As<int>(),
                LikesCount = record["likesCount"].As<int>(),
                HasLiked = record["hasLiked"].As<bool>()
            }).ToList();
        });

        return Results.Ok(posts);
    }).RequireAuthorization();

    // ========== COMMENT ENDPOINTS ==========

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
            // Create comment and increment post commentsCount atomically
            var query = @"
                MATCH (p:Post {id: $postId})
                CREATE (c:Comment {
                    id: $id,
                    postId: $postId,
                    userId: $userId,
                    content: $content,
                    createdAt: datetime($createdAt)
                })
                SET p.commentsCount = p.commentsCount + 1
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
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
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
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
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
            // Check if comment exists and belongs to user, then delete and decrement post commentsCount
            var query = @"
                MATCH (c:Comment {id: $commentId, userId: $userId})
                MATCH (p:Post {id: c.postId})
                DELETE c
                SET p.commentsCount = p.commentsCount - 1
                RETURN count(c) AS deleted
            ";

            var cursor = await tx.RunAsync(query, new { commentId, userId });
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

    // ========== LIKE ENDPOINTS ==========

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
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
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

    Log.Information("Content API (Neo4j - Posts, Comments, Likes) starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Content API failed to start");
}
finally
{
    Log.CloseAndFlush();
}

// Helper method to safely convert Neo4j values to string arrays
static string[] ConvertToStringArray(object value)
{
    if (value == null)
        return Array.Empty<string>();

    if (value is IList<object> list)
        return list.Select(item => item?.ToString() ?? string.Empty).ToArray();

    if (value is string[] stringArray)
        return stringArray;

    if (value is IEnumerable<string> stringEnumerable)
        return stringEnumerable.ToArray();

    return Array.Empty<string>();
}

// Helper method to safely convert Neo4j temporal values to DateTime
static DateTime ConvertToDateTime(object value)
{
    if (value == null)
        return DateTime.MinValue;

    // Handle Neo4j.Driver temporal types (ZonedDateTime, LocalDateTime)
    var valueType = value.GetType();

    // Check if it has ToDateTimeOffset() method (ZonedDateTime)
    var toDateTimeOffsetMethod = valueType.GetMethod("ToDateTimeOffset");
    if (toDateTimeOffsetMethod != null)
    {
        var dateTimeOffset = (DateTimeOffset)toDateTimeOffsetMethod.Invoke(value, null);
        return dateTimeOffset.DateTime;
    }

    // Check if it has ToDateTimeUnspecified() method (LocalDateTime)
    var toDateTimeUnspecifiedMethod = valueType.GetMethod("ToDateTimeUnspecified");
    if (toDateTimeUnspecifiedMethod != null)
    {
        return (DateTime)toDateTimeUnspecifiedMethod.Invoke(value, null);
    }

    if (value is DateTime dateTime)
        return dateTime;

    // Fallback: try to parse as string
    if (value is string str && DateTime.TryParse(str, out var parsed))
        return parsed;

    return DateTime.MinValue;
}

// ========== MODELS ==========

public class Post
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public string MediaUrl { get; set; } = string.Empty;
    public string[] Hashtags { get; set; } = Array.Empty<string>();
    public string[] ExerciseIds { get; set; } = Array.Empty<string>();
    public DateTime CreatedAt { get; set; }
    public int CommentsCount { get; set; }
    public int LikesCount { get; set; }
    public bool HasLiked { get; set; }
}

public class Comment
{
    public string Id { get; set; } = string.Empty;
    public string PostId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class Like
{
    public string Id { get; set; } = string.Empty;
    public string PostId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public record CreatePostRequest(string? Caption, string? MediaUrl, string[]? Hashtags, Guid[]? ExerciseIds);
public record CreateCommentRequest(Guid PostId, string Content);
