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
    builder.Services.AddDbContext<CommentsLikesDbContext>(options =>
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
        var db = scope.ServiceProvider.GetRequiredService<CommentsLikesDbContext>();
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
    app.MapGet("/api/commentslikes/health", () => Results.Ok(new { status = "Healthy", service = "CommentsLikes API" }));

    // Create comment
    app.MapPost("/api/commentslikes/comments", async (HttpContext context, CreateCommentRequest request, CommentsLikesDbContext db) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var comment = new Comment
        {
            Id = Guid.NewGuid(),
            PostId = request.PostId,
            UserId = Guid.Parse(userId),
            Content = request.Content,
            CreatedAt = DateTime.UtcNow
        };

        db.Comments.Add(comment);
        await db.SaveChangesAsync();

        Log.Information("Comment created: {CommentId} on post {PostId} by user {UserId}", comment.Id, request.PostId, userId);

        return Results.Ok(comment);
    }).RequireAuthorization();

    // Get comments for post
    app.MapGet("/api/commentslikes/comments/post/{postId:guid}", async (Guid postId, CommentsLikesDbContext db) =>
    {
        var comments = await db.Comments
            .Where(c => c.PostId == postId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        return Results.Ok(comments);
    }).RequireAuthorization();

    // Like a post
    app.MapPost("/api/commentslikes/likes", async (HttpContext context, CreateLikeRequest request, CommentsLikesDbContext db) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var userGuid = Guid.Parse(userId);

        // Check if already liked
        var existingLike = await db.Likes.FirstOrDefaultAsync(l => l.PostId == request.PostId && l.UserId == userGuid);
        if (existingLike != null)
        {
            return Results.BadRequest(new { error = "Post already liked" });
        }

        var like = new Like
        {
            Id = Guid.NewGuid(),
            PostId = request.PostId,
            UserId = userGuid,
            CreatedAt = DateTime.UtcNow
        };

        db.Likes.Add(like);
        await db.SaveChangesAsync();

        Log.Information("Post {PostId} liked by user {UserId}", request.PostId, userId);

        return Results.Ok(like);
    }).RequireAuthorization();

    // Unlike a post
    app.MapDelete("/api/commentslikes/likes/{postId:guid}", async (Guid postId, HttpContext context, CommentsLikesDbContext db) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var userGuid = Guid.Parse(userId);
        var like = await db.Likes.FirstOrDefaultAsync(l => l.PostId == postId && l.UserId == userGuid);

        if (like == null)
        {
            return Results.NotFound(new { error = "Like not found" });
        }

        db.Likes.Remove(like);
        await db.SaveChangesAsync();

        Log.Information("Post {PostId} unliked by user {UserId}", postId, userId);

        return Results.Ok(new { message = "Like removed" });
    }).RequireAuthorization();

    // Get like count for post
    app.MapGet("/api/commentslikes/likes/post/{postId:guid}", async (Guid postId, CommentsLikesDbContext db) =>
    {
        var count = await db.Likes.CountAsync(l => l.PostId == postId);

        return Results.Ok(new { postId, likesCount = count });
    }).RequireAuthorization();

    Log.Information("CommentsLikes API starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "CommentsLikes API failed to start");
}
finally
{
    Log.CloseAndFlush();
}

// Database Context
public class CommentsLikesDbContext : DbContext
{
    public CommentsLikesDbContext(DbContextOptions<CommentsLikesDbContext> options) : base(options) { }

    public DbSet<Comment> Comments { get; set; }
    public DbSet<Like> Likes { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Comment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PostId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<Like>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PostId);
            entity.HasIndex(e => new { e.PostId, e.UserId }).IsUnique();
        });
    }
}

// Models
public class Comment
{
    public Guid Id { get; set; }
    public Guid PostId { get; set; }
    public Guid UserId { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class Like
{
    public Guid Id { get; set; }
    public Guid PostId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public record CreateCommentRequest(Guid PostId, string Content);
public record CreateLikeRequest(Guid PostId);
