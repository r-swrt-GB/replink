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
            CreatedAt = DateTime.UtcNow,
            LikesCount = 0,
            CommentsCount = 0
        };

        db.Posts.Add(post);
        await db.SaveChangesAsync();

        Log.Information("Post created: {PostId} by user {UserId}", post.Id, userId);

        return Results.Ok(post);
    }).RequireAuthorization();

    // Get all posts
    app.MapGet("/api/posts", async (PostsDbContext db, int? limit, int? offset) =>
    {
        var query = db.Posts.OrderByDescending(p => p.CreatedAt);

        var posts = await query
            .Skip(offset ?? 0)
            .Take(limit ?? 20)
            .ToListAsync();

        return Results.Ok(posts);
    }).RequireAuthorization();

    // Get single post
    app.MapGet("/api/posts/{id:guid}", async (Guid id, PostsDbContext db) =>
    {
        var post = await db.Posts.FindAsync(id);

        if (post == null)
        {
            return Results.NotFound(new { error = "Post not found" });
        }

        return Results.Ok(post);
    }).RequireAuthorization();

    // Get user's posts
    app.MapGet("/api/posts/user/{userId:guid}", async (Guid userId, PostsDbContext db, int? limit, int? offset) =>
    {
        var posts = await db.Posts
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .Skip(offset ?? 0)
            .Take(limit ?? 20)
            .ToListAsync();

        return Results.Ok(posts);
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
    public DateTime CreatedAt { get; set; }
    public int LikesCount { get; set; }
    public int CommentsCount { get; set; }
}

public record CreatePostRequest(string? Caption, string? MediaUrl, string[]? Hashtags);
