using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Text;
using System.Security.Claims;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // Add DbContext
    var connectionString = builder.Configuration["ConnectionStrings:DefaultConnection"];
    builder.Services.AddDbContext<PostsDbContext>(options =>
        options.UseNpgsql(connectionString));

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

    // Auto-create database
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<PostsDbContext>();
        db.Database.EnsureCreated();
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseAuthentication();
    app.UseAuthorization();

    // Health endpoint
    app.MapGet("/api/posts/health", () => Results.Ok(new { status = "Healthy", service = "Posts API" }));

    // Create post
    app.MapPost("/api/posts", async (HttpContext context, CreatePostRequest request, PostsDbContext db) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var post = new Post
        {
            Id = Guid.NewGuid(),
            UserId = Guid.Parse(userId),
            Caption = request.Caption ?? string.Empty,
            MediaUrl = request.MediaUrl ?? string.Empty,
            Hashtags = request.Hashtags ?? Array.Empty<string>(),
            ExerciseIds = request.ExerciseIds ?? Array.Empty<Guid>(),
            CreatedAt = DateTime.UtcNow,
            CommentsCount = 0
        };

        db.Posts.Add(post);
        await db.SaveChangesAsync();

        Log.Information("Post created: {PostId} by user {UserId}", post.Id, userId);

        return Results.Ok(post);
    }).RequireAuthorization();

    // Get all posts
    app.MapGet("/api/posts", async (HttpContext context, PostsDbContext db, IHttpClientFactory httpClientFactory, int? limit, int? offset) =>
    {
        var query = db.Posts.OrderByDescending(p => p.CreatedAt);

        var posts = await query
            .Skip(offset ?? 0)
            .Take(limit ?? 20)
            .ToListAsync();

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
                    post.CreatedAt,
                    LikesCount = 0,
                    post.CommentsCount
                });
            }
        }

        return Results.Ok(postsWithLikes);
    }).RequireAuthorization();

    // Get single post
    app.MapGet("/api/posts/{id:guid}", async (Guid id, HttpContext context, PostsDbContext db, IHttpClientFactory httpClientFactory) =>
    {
        var post = await db.Posts.FindAsync(id);

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
    app.MapGet("/api/posts/user/{userId:guid}", async (Guid userId, HttpContext context, PostsDbContext db, IHttpClientFactory httpClientFactory, int? limit, int? offset) =>
    {
        var posts = await db.Posts
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .Skip(offset ?? 0)
            .Take(limit ?? 20)
            .ToListAsync();

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
                    post.CreatedAt,
                    LikesCount = 0,
                    post.CommentsCount
                });
            }
        }

        return Results.Ok(postsWithLikes);
    }).RequireAuthorization();

    Log.Information("Posts API starting on port 80");

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

// Database Context
public class PostsDbContext : DbContext
{
    public PostsDbContext(DbContextOptions<PostsDbContext> options) : base(options) { }

    public DbSet<Post> Posts { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Post>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CreatedAt);
        });
    }
}

// Models
public class Post
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Caption { get; set; } = string.Empty;
    public string MediaUrl { get; set; } = string.Empty;
    public string[] Hashtags { get; set; } = Array.Empty<string>();
    public Guid[] ExerciseIds { get; set; } = Array.Empty<Guid>();
    public DateTime CreatedAt { get; set; }
    public int CommentsCount { get; set; }
}

public record CreatePostRequest(string? Caption, string? MediaUrl, string[]? Hashtags, Guid[]? ExerciseIds);

public record LikesResponse(Guid PostId, int LikesCount);
