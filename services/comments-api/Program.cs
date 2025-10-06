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
    
    builder.Services.AddDbContext<CommentsDbContext>(options =>
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
        var db = scope.ServiceProvider.GetRequiredService<CommentsDbContext>();
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
    app.MapGet("/api/health", () => Results.Ok(new { status = "Healthy", service = "Comments API" }));

    // Create comment
    app.MapPost("/api/comments", async (HttpContext context, CreateCommentRequest request, CommentsDbContext db) =>
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
    app.MapGet("/api/posts/{postId:guid}/comments", async (Guid postId, CommentsDbContext db) =>
    {
        var comments = await db.Comments
            .Where(c => c.PostId == postId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        return Results.Ok(comments);
    }).RequireAuthorization();

    // Delete comment
    app.MapDelete("/api/comments/{commentId:guid}", async (Guid commentId, HttpContext context, CommentsDbContext db) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var userGuid = Guid.Parse(userId);
        var comment = await db.Comments.FirstOrDefaultAsync(c => c.Id == commentId && c.UserId == userGuid);

        if (comment == null)
        {
            return Results.NotFound(new { error = "Comment not found or unauthorized" });
        }

        db.Comments.Remove(comment);
        await db.SaveChangesAsync();

        Log.Information("Comment {CommentId} deleted by user {UserId}", commentId, userId);

        return Results.Ok(new { message = "Comment deleted" });
    }).RequireAuthorization();

    Log.Information("Comments API starting on port 80");

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

// Database Context
public class CommentsDbContext : DbContext
{
    public CommentsDbContext(DbContextOptions<CommentsDbContext> options) : base(options) { }

    public DbSet<Comment> Comments { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Comment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PostId);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CreatedAt);
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

public record CreateCommentRequest(Guid PostId, string Content);