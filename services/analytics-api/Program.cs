using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Text;
using System.Security.Claims;
using StackExchange.Redis;
using System.Text.Json;
using Polly;
using Polly.Extensions.Http;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // Add Redis
    var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "redis:6379";
    builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConnectionString));

    // Polly retry policy for transient failures
    var retryPolicy = HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
            onRetry: (outcome, timespan, retryAttempt, context) =>
            {
                Log.Warning("Retry {RetryAttempt} after {Delay}s due to {Reason}",
                    retryAttempt, timespan.TotalSeconds, outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString());
            });

    // Add HttpClient for calling other services
    builder.Services.AddHttpClient("ContentApi", client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["Services:ContentApi"] ?? "http://content-api:80");
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddPolicyHandler(retryPolicy);

    builder.Services.AddHttpClient("FitnessApi", client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["Services:FitnessApi"] ?? "http://fitness-api:80");
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddPolicyHandler(retryPolicy);

    builder.Services.AddHttpClient("SocialGraphApi", client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["Services:SocialGraphApi"] ?? "http://socialgraph-api:80");
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddPolicyHandler(retryPolicy);

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

    // Health endpoint with Redis connectivity check
    app.MapGet("/api/analytics/health", async (IConnectionMultiplexer redis) =>
    {
        try
        {
            var redisHealthy = true;
            var redisError = "";

            try
            {
                var db = redis.GetDatabase();
                await db.PingAsync();
            }
            catch (Exception ex)
            {
                redisHealthy = false;
                redisError = ex.Message;
                Log.Warning(ex, "Redis health check failed");
            }

            return Results.Ok(new
            {
                status = "Healthy",
                service = "Analytics API",
                cache = redisHealthy ? "Redis - Connected" : $"Redis - Disconnected: {redisError}",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Health check failed for Analytics API");
            return Results.Problem(new
            {
                status = "Unhealthy",
                service = "Analytics API",
                error = ex.Message,
                timestamp = DateTime.UtcNow
            }.ToString());
        }
    });

    // Get user analytics
    app.MapGet("/api/analytics/user/{userId:guid}", async (
        Guid userId,
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        IConnectionMultiplexer redis) =>
    {
        var db = redis.GetDatabase();
        var cacheKey = $"analytics:user:{userId}";

        // Try to get from cache
        var cachedData = await db.StringGetAsync(cacheKey);
        if (cachedData.HasValue)
        {
            Log.Information("Analytics cache hit for user {UserId}", userId);
            return Results.Ok(JsonSerializer.Deserialize<UserAnalytics>(cachedData!));
        }

        Log.Information("Analytics cache miss for user {UserId}, aggregating data", userId);

        // Get auth token from request
        var token = context.Request.Headers["Authorization"].ToString();

        var analytics = new UserAnalytics
        {
            UserId = userId,
            PostsCount = 0,
            WorkoutsCount = 0,
            FollowersCount = 0,
            FollowingCount = 0,
            TotalLikes = 0,
            TotalComments = 0,
            LastUpdated = DateTime.UtcNow
        };

        // Get posts count from Content API
        try
        {
            var contentClient = httpClientFactory.CreateClient("ContentApi");
            if (!string.IsNullOrEmpty(token))
            {
                contentClient.DefaultRequestHeaders.Add("Authorization", token);
            }
            var postsResponse = await contentClient.GetAsync($"/api/posts/user/{userId}");
            if (postsResponse.IsSuccessStatusCode)
            {
                var posts = await postsResponse.Content.ReadFromJsonAsync<List<PostDto>>();
                analytics.PostsCount = posts?.Count ?? 0;

                // Calculate total likes and comments from posts
                if (posts != null)
                {
                    analytics.TotalLikes = posts.Sum(p => p.LikesCount);
                    analytics.TotalComments = posts.Sum(p => p.CommentsCount);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch posts analytics for user {UserId}", userId);
        }

        // Get workouts count from Fitness API
        try
        {
            var fitnessClient = httpClientFactory.CreateClient("FitnessApi");
            if (!string.IsNullOrEmpty(token))
            {
                fitnessClient.DefaultRequestHeaders.Add("Authorization", token);
            }
            var workoutsResponse = await fitnessClient.GetAsync($"/api/workouts/user/{userId}");
            if (workoutsResponse.IsSuccessStatusCode)
            {
                var workouts = await workoutsResponse.Content.ReadFromJsonAsync<List<WorkoutDto>>();
                analytics.WorkoutsCount = workouts?.Count ?? 0;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch workouts analytics for user {UserId}", userId);
        }

        // Get followers/following count
        try
        {
            var graphClient = httpClientFactory.CreateClient("SocialGraphApi");
            if (!string.IsNullOrEmpty(token))
            {
                graphClient.DefaultRequestHeaders.Add("Authorization", token);
            }

            var followersResponse = await graphClient.GetAsync($"/api/graph/followers/{userId}");
            if (followersResponse.IsSuccessStatusCode)
            {
                var followers = await followersResponse.Content.ReadFromJsonAsync<List<FollowerDto>>();
                analytics.FollowersCount = followers?.Count ?? 0;
            }

            var followingResponse = await graphClient.GetAsync($"/api/graph/following/{userId}");
            if (followingResponse.IsSuccessStatusCode)
            {
                var following = await followingResponse.Content.ReadFromJsonAsync<List<FollowerDto>>();
                analytics.FollowingCount = following?.Count ?? 0;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch social graph analytics for user {UserId}", userId);
        }

        // Cache for 5 minutes
        await db.StringSetAsync(cacheKey, JsonSerializer.Serialize(analytics), TimeSpan.FromMinutes(5));

        return Results.Ok(analytics);
    }).RequireAuthorization();

    // Get global analytics
    app.MapGet("/api/analytics/global", async (
        IHttpClientFactory httpClientFactory,
        IConnectionMultiplexer redis,
        HttpContext context) =>
    {
        var db = redis.GetDatabase();
        var cacheKey = "analytics:global";

        // Try to get from cache
        var cachedData = await db.StringGetAsync(cacheKey);
        if (cachedData.HasValue)
        {
            Log.Information("Global analytics cache hit");
            return Results.Ok(JsonSerializer.Deserialize<GlobalAnalytics>(cachedData!));
        }

        Log.Information("Global analytics cache miss, aggregating data");

        var token = context.Request.Headers["Authorization"].ToString();

        var analytics = new GlobalAnalytics
        {
            TotalPosts = 0,
            TotalWorkouts = 0,
            TotalLikes = 0,
            TotalComments = 0,
            LastUpdated = DateTime.UtcNow
        };

        // Get total posts from Content API
        try
        {
            var contentClient = httpClientFactory.CreateClient("ContentApi");
            if (!string.IsNullOrEmpty(token))
            {
                contentClient.DefaultRequestHeaders.Add("Authorization", token);
            }
            var postsResponse = await contentClient.GetAsync("/api/posts?limit=10000");
            if (postsResponse.IsSuccessStatusCode)
            {
                var posts = await postsResponse.Content.ReadFromJsonAsync<List<PostDto>>();
                analytics.TotalPosts = posts?.Count ?? 0;
                if (posts != null)
                {
                    analytics.TotalLikes = posts.Sum(p => p.LikesCount);
                    analytics.TotalComments = posts.Sum(p => p.CommentsCount);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch global posts analytics");
        }

        // Get total workouts from Fitness API
        try
        {
            var fitnessClient = httpClientFactory.CreateClient("FitnessApi");
            if (!string.IsNullOrEmpty(token))
            {
                fitnessClient.DefaultRequestHeaders.Add("Authorization", token);
            }
            var workoutsResponse = await fitnessClient.GetAsync("/api/workouts?limit=10000");
            if (workoutsResponse.IsSuccessStatusCode)
            {
                var workouts = await workoutsResponse.Content.ReadFromJsonAsync<List<WorkoutDto>>();
                analytics.TotalWorkouts = workouts?.Count ?? 0;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch global workouts analytics");
        }

        // Cache for 10 minutes
        await db.StringSetAsync(cacheKey, JsonSerializer.Serialize(analytics), TimeSpan.FromMinutes(10));

        return Results.Ok(analytics);
    }).RequireAuthorization();

    Log.Information("Analytics API starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Analytics API failed to start");
}
finally
{
    Log.CloseAndFlush();
}

// DTOs
public class UserAnalytics
{
    public Guid UserId { get; set; }
    public int PostsCount { get; set; }
    public int WorkoutsCount { get; set; }
    public int FollowersCount { get; set; }
    public int FollowingCount { get; set; }
    public int TotalLikes { get; set; }
    public int TotalComments { get; set; }
    public DateTime LastUpdated { get; set; }
}

public class GlobalAnalytics
{
    public int TotalPosts { get; set; }
    public int TotalWorkouts { get; set; }
    public int TotalLikes { get; set; }
    public int TotalComments { get; set; }
    public DateTime LastUpdated { get; set; }
}

public class PostDto
{
    public Guid Id { get; set; }
    public int LikesCount { get; set; }
    public int CommentsCount { get; set; }
}

public class WorkoutDto
{
    public Guid Id { get; set; }
}

public class FollowerDto
{
    public string Id { get; set; } = string.Empty;
}
