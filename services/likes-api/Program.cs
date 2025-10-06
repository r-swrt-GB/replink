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
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    
    if (string.IsNullOrEmpty(connectionString))
    {
        Log.Fatal("Database connection string is missing or empty");
        throw new InvalidOperationException("Database connection string 'DefaultConnection' is required");
    }
    
    Log.Information("Using connection string: {ConnectionString}", connectionString.Replace("Password=", "Password=***"));
    
    builder.Services.AddDbContext<LikesDbContext>(options =>
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
        var db = scope.ServiceProvider.GetRequiredService<LikesDbContext>();
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
    app.MapGet("/api/health", () => Results.Ok(new { status = "Healthy", service = "Likes API" }));

    // Like a post
    app.MapPost("/api/posts/{postId:guid}/likes", async (Guid postId, HttpContext context, LikesDbContext db) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var userGuid = Guid.Parse(userId);

        // Check if already liked
        var existingLike = await db.Likes.FirstOrDefaultAsync(l => l.PostId == postId && l.UserId == userGuid);
        if (existingLike != null)
        {
            return Results.BadRequest(new { error = "Post already liked" });
        }

        var like = new Like
        {
            Id = Guid.NewGuid(),
            PostId = postId,
            UserId = userGuid,
            CreatedAt = DateTime.UtcNow
        };

        db.Likes.Add(like);
        await db.SaveChangesAsync();

        Log.Information("Post {PostId} liked by user {UserId}", postId, userId);

        return Results.Ok(like);
    }).RequireAuthorization();

    // Unlike a post
    app.MapDelete("/api/posts/{postId:guid}/likes", async (Guid postId, HttpContext context, LikesDbContext db) =>
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
    app.MapGet("/api/posts/{postId:guid}/likes", async (Guid postId, LikesDbContext db) =>
    {
        var count = await db.Likes.CountAsync(l => l.PostId == postId);

        return Results.Ok(new { postId, likesCount = count });
    }).RequireAuthorization();

    // Check if user liked a post
    app.MapGet("/api/posts/{postId:guid}/likes/me", async (Guid postId, HttpContext context, LikesDbContext db) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var userGuid = Guid.Parse(userId);
        var hasLiked = await db.Likes.AnyAsync(l => l.PostId == postId && l.UserId == userGuid);

        return Results.Ok(new { postId, hasLiked });
    }).RequireAuthorization();

    Log.Information("Likes API starting on port 80");

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

// Database Context
public class LikesDbContext : DbContext
{
    public LikesDbContext(DbContextOptions<LikesDbContext> options) : base(options) { }

    public DbSet<Like> Likes { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Like>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PostId);
            entity.HasIndex(e => new { e.PostId, e.UserId }).IsUnique();
        });
    }
}

// Models
public class Like
{
    public Guid Id { get; set; }
    public Guid PostId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
}