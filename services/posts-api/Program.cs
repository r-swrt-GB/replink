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

    // Add HttpClient for calling Likes API
    builder.Services.AddHttpClient("LikesApi", client =>
    {
        client.BaseAddress = new Uri("http://likes-api:80");
    });

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
                // Create unique constraint on Post.id
                await tx.RunAsync("CREATE CONSTRAINT post_id_unique IF NOT EXISTS FOR (p:Post) REQUIRE p.id IS UNIQUE");

                // Create index on Post.userId for faster queries
                await tx.RunAsync("CREATE INDEX post_userid_index IF NOT EXISTS FOR (p:Post) ON (p.userId)");

                // Create index on Post.createdAt for ordering
                await tx.RunAsync("CREATE INDEX post_createdat_index IF NOT EXISTS FOR (p:Post) ON (p.createdAt)");
            });
            Log.Information("Neo4j constraints and indexes created for Posts");
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
    app.MapGet("/api/posts/health", () => Results.Ok(new { status = "Healthy", service = "Posts API (Neo4j)" }));

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
                Hashtags = record["hashtags"].As<string[]>(),
                ExerciseIds = record["exerciseIds"].As<string[]>(),
                CreatedAt = record["createdAt"].As<DateTime>(),
                CommentsCount = record["commentsCount"].As<int>()
            };
        });

        Log.Information("Post created: {PostId} by user {UserId}", postId, userId);

        return Results.Ok(post);
    }).RequireAuthorization();

    // Get all posts
    app.MapGet("/api/posts", async (HttpContext context, IDriver driver, IHttpClientFactory httpClientFactory, int? limit, int? offset) =>
    {
        await using var session = driver.AsyncSession();
        var posts = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (p:Post)
                RETURN p.id AS id,
                       p.userId AS userId,
                       p.caption AS caption,
                       p.mediaUrl AS mediaUrl,
                       p.hashtags AS hashtags,
                       p.exerciseIds AS exerciseIds,
                       p.createdAt AS createdAt,
                       p.commentsCount AS commentsCount
                ORDER BY p.createdAt DESC
                SKIP $offset
                LIMIT $limit
            ";

            var cursor = await tx.RunAsync(query, new {
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
                Hashtags = record["hashtags"].As<string[]>(),
                ExerciseIds = record["exerciseIds"].As<string[]>(),
                CreatedAt = record["createdAt"].As<DateTime>(),
                CommentsCount = record["commentsCount"].As<int>()
            }).ToList();
        });

        // Fetch like counts for all posts
        var httpClient = httpClientFactory.CreateClient("LikesApi");

        // Forward the Authorization header
        var token = context.Request.Headers["Authorization"].ToString();
        if (!string.IsNullOrEmpty(token))
        {
            httpClient.DefaultRequestHeaders.Add("Authorization", token);
        }

        var postsWithLikes = new List<object>();

        foreach (var post in posts)
        {
            try
            {
                var response = await httpClient.GetAsync($"/api/posts/{post.Id}/likes");
                var likesCount = 0;
                if (response.IsSuccessStatusCode)
                {
                    var likesData = await response.Content.ReadFromJsonAsync<LikesResponse>();
                    likesCount = likesData?.LikesCount ?? 0;
                }

                postsWithLikes.Add(new
                {
                    post.Id,
                    post.UserId,
                    post.Caption,
                    post.MediaUrl,
                    post.Hashtags,
                    post.ExerciseIds,
                    post.CreatedAt,
                    LikesCount = likesCount,
                    post.CommentsCount
                });
            }
            catch
            {
                postsWithLikes.Add(new
                {
                    post.Id,
                    post.UserId,
                    post.Caption,
                    post.MediaUrl,
                    post.Hashtags,
                    post.ExerciseIds,
                    post.CreatedAt,
                    LikesCount = 0,
                    post.CommentsCount
                });
            }
        }

        return Results.Ok(postsWithLikes);
    }).RequireAuthorization();

    // Get single post
    app.MapGet("/api/posts/{id}", async (string id, HttpContext context, IDriver driver, IHttpClientFactory httpClientFactory) =>
    {
        await using var session = driver.AsyncSession();
        var post = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (p:Post {id: $id})
                RETURN p.id AS id,
                       p.userId AS userId,
                       p.caption AS caption,
                       p.mediaUrl AS mediaUrl,
                       p.hashtags AS hashtags,
                       p.exerciseIds AS exerciseIds,
                       p.createdAt AS createdAt,
                       p.commentsCount AS commentsCount
            ";

            var cursor = await tx.RunAsync(query, new { id });
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
                Hashtags = record["hashtags"].As<string[]>(),
                ExerciseIds = record["exerciseIds"].As<string[]>(),
                CreatedAt = record["createdAt"].As<DateTime>(),
                CommentsCount = record["commentsCount"].As<int>()
            };
        });

        if (post == null)
        {
            return Results.NotFound(new { error = "Post not found" });
        }

        // Fetch like count from Likes API
        var httpClient = httpClientFactory.CreateClient("LikesApi");
        var likesCount = 0;
        try
        {
            // Forward the Authorization header
            var token = context.Request.Headers["Authorization"].ToString();
            if (!string.IsNullOrEmpty(token))
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", token);
            }

            var response = await httpClient.GetAsync($"/api/posts/{post.Id}/likes");
            if (response.IsSuccessStatusCode)
            {
                var likesData = await response.Content.ReadFromJsonAsync<LikesResponse>();
                likesCount = likesData?.LikesCount ?? 0;
            }
        }
        catch
        {
            // If Likes API fails, default to 0
        }

        var postWithLikes = new
        {
            post.Id,
            post.UserId,
            post.Caption,
            post.MediaUrl,
            post.Hashtags,
            post.ExerciseIds,
            post.CreatedAt,
            LikesCount = likesCount,
            post.CommentsCount
        };

        return Results.Ok(postWithLikes);
    }).RequireAuthorization();

    // Get user's posts
    app.MapGet("/api/posts/user/{userId}", async (string userId, HttpContext context, IDriver driver, IHttpClientFactory httpClientFactory, int? limit, int? offset) =>
    {
        await using var session = driver.AsyncSession();
        var posts = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (p:Post {userId: $userId})
                RETURN p.id AS id,
                       p.userId AS userId,
                       p.caption AS caption,
                       p.mediaUrl AS mediaUrl,
                       p.hashtags AS hashtags,
                       p.exerciseIds AS exerciseIds,
                       p.createdAt AS createdAt,
                       p.commentsCount AS commentsCount
                ORDER BY p.createdAt DESC
                SKIP $offset
                LIMIT $limit
            ";

            var cursor = await tx.RunAsync(query, new {
                userId,
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
                Hashtags = record["hashtags"].As<string[]>(),
                ExerciseIds = record["exerciseIds"].As<string[]>(),
                CreatedAt = record["createdAt"].As<DateTime>(),
                CommentsCount = record["commentsCount"].As<int>()
            }).ToList();
        });

        // Fetch like counts for all posts
        var httpClient = httpClientFactory.CreateClient("LikesApi");

        // Forward the Authorization header
        var token = context.Request.Headers["Authorization"].ToString();
        if (!string.IsNullOrEmpty(token))
        {
            httpClient.DefaultRequestHeaders.Add("Authorization", token);
        }

        var postsWithLikes = new List<object>();

        foreach (var post in posts)
        {
            try
            {
                var response = await httpClient.GetAsync($"/api/posts/{post.Id}/likes");
                var likesCount = 0;
                if (response.IsSuccessStatusCode)
                {
                    var likesData = await response.Content.ReadFromJsonAsync<LikesResponse>();
                    likesCount = likesData?.LikesCount ?? 0;
                }

                postsWithLikes.Add(new
                {
                    post.Id,
                    post.UserId,
                    post.Caption,
                    post.MediaUrl,
                    post.Hashtags,
                    post.ExerciseIds,
                    post.CreatedAt,
                    LikesCount = likesCount,
                    post.CommentsCount
                });
            }
            catch
            {
                postsWithLikes.Add(new
                {
                    post.Id,
                    post.UserId,
                    post.Caption,
                    post.MediaUrl,
                    post.Hashtags,
                    post.ExerciseIds,
                    post.CreatedAt,
                    LikesCount = 0,
                    post.CommentsCount
                });
            }
        }

        return Results.Ok(postsWithLikes);
    }).RequireAuthorization();

    Log.Information("Posts API (Neo4j) starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Posts API failed to start");
}
finally
{
    Log.CloseAndFlush();
}

// Models
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
}

public record CreatePostRequest(string? Caption, string? MediaUrl, string[]? Hashtags, Guid[]? ExerciseIds);

public record LikesResponse(string PostId, int LikesCount);
