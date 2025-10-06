using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Text;
using System.Security.Claims;
using System.Text.Json;
using StackExchange.Redis;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // HttpClient for calling other APIs
    builder.Services.AddHttpClient("PostsApi", client =>
    {
        var postsApiUrl = builder.Configuration["Services:PostsApi"] ?? "http://localhost:5002";
        client.BaseAddress = new Uri(postsApiUrl);
    });

    builder.Services.AddHttpClient("SocialGraphApi", client =>
    {
        var socialGraphApiUrl = builder.Configuration["Services:SocialGraphApi"] ?? "http://localhost:5004";
        client.BaseAddress = new Uri(socialGraphApiUrl);
    });

    // Redis for caching (optional)
    var redisConnection = builder.Configuration["Redis:ConnectionString"];
    if (!string.IsNullOrEmpty(redisConnection))
    {
        builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConnection));
    }

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
    app.MapGet("/api/feed/health", () => Results.Ok(new { status = "Healthy", service = "Feed API" }));

    // Get feed (aggregated posts from followed users)
    app.MapGet("/api/feed", async (HttpContext context, IHttpClientFactory httpClientFactory, IConnectionMultiplexer? redis) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var token = context.Request.Headers.Authorization.ToString().Replace("Bearer ", "");

        try
        {
            // Check cache first (if Redis is configured)
            if (redis != null)
            {
                var db = redis.GetDatabase();
                var cachedFeed = await db.StringGetAsync($"feed:{userId}");
                if (!cachedFeed.IsNullOrEmpty)
                {
                    Log.Information("Feed cache hit for user {UserId}", userId);
                    var cachedPosts = JsonSerializer.Deserialize<List<object>>(cachedFeed!);
                    return Results.Ok(cachedPosts);
                }
            }

            // Step 1: Get followed user IDs from Social Graph API
            var socialGraphClient = httpClientFactory.CreateClient("SocialGraphApi");
            socialGraphClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            var feedSourcesResponse = await socialGraphClient.GetAsync("/api/graph/feed-sources");
            if (!feedSourcesResponse.IsSuccessStatusCode)
            {
                Log.Warning("Failed to get feed sources for user {UserId}", userId);
                return Results.Ok(new { posts = new List<object>() });
            }

            var feedSourcesJson = await feedSourcesResponse.Content.ReadAsStringAsync();
            var feedSources = JsonSerializer.Deserialize<FeedSourcesResponse>(feedSourcesJson);

            if (feedSources?.UserIds == null || feedSources.UserIds.Count == 0)
            {
                Log.Information("User {UserId} is not following anyone", userId);
                return Results.Ok(new { posts = new List<object>() });
            }

            // Step 2: Get posts from Posts API for each followed user
            var postsClient = httpClientFactory.CreateClient("PostsApi");
            postsClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            var allPosts = new List<object>();

            foreach (var followedUserId in feedSources.UserIds)
            {
                var postsResponse = await postsClient.GetAsync($"/api/posts/user/{followedUserId}?limit=10");
                if (postsResponse.IsSuccessStatusCode)
                {
                    var postsJson = await postsResponse.Content.ReadAsStringAsync();
                    var posts = JsonSerializer.Deserialize<List<object>>(postsJson);
                    if (posts != null)
                    {
                        allPosts.AddRange(posts);
                    }
                }
            }

            Log.Information("Retrieved {Count} posts for user {UserId} feed", allPosts.Count, userId);

            // Cache the results (if Redis is configured)
            if (redis != null && allPosts.Any())
            {
                var db = redis.GetDatabase();
                var serialized = JsonSerializer.Serialize(allPosts);
                await db.StringSetAsync($"feed:{userId}", serialized, TimeSpan.FromMinutes(5));
            }

            return Results.Ok(new { posts = allPosts });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error generating feed for user {UserId}", userId);
            return Results.Problem("Failed to generate feed");
        }
    }).RequireAuthorization();

    Log.Information("Feed API starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Feed API failed to start");
}
finally
{
    Log.CloseAndFlush();
}

// Response models
public class FeedSourcesResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("userIds")]
    public List<string> UserIds { get; set; } = new();
}
