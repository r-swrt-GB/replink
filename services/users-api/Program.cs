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
    builder.Services.AddDbContext<UsersDbContext>(options =>
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
        var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
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
    app.MapGet("/api/users/health", () => Results.Ok(new { status = "Healthy", service = "Users API" }));

    // Get current user profile
    app.MapGet("/api/users/profile", async (HttpContext context, UsersDbContext db) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == Guid.Parse(userId));

        if (profile == null)
        {
            return Results.NotFound(new { error = "Profile not found" });
        }

        return Results.Ok(profile);
    }).RequireAuthorization();

    // Create/Update user profile
    app.MapPost("/api/users/profile", async (HttpContext context, UserProfileRequest request, UsersDbContext db) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var userGuid = Guid.Parse(userId);
        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userGuid);

        if (profile == null)
        {
            // Create new profile
            profile = new UserProfile
            {
                Id = Guid.NewGuid(),
                UserId = userGuid,
                DisplayName = request.DisplayName,
                Bio = request.Bio ?? string.Empty,
                AvatarUrl = request.AvatarUrl ?? string.Empty,
                CreatedAt = DateTime.UtcNow
            };
            db.UserProfiles.Add(profile);
            Log.Information("Created profile for user {UserId}", userId);
        }
        else
        {
            // Update existing profile
            profile.DisplayName = request.DisplayName;
            profile.Bio = request.Bio ?? profile.Bio;
            profile.AvatarUrl = request.AvatarUrl ?? profile.AvatarUrl;
            Log.Information("Updated profile for user {UserId}", userId);
        }

        await db.SaveChangesAsync();

        return Results.Ok(profile);
    }).RequireAuthorization();

    // Search users
    app.MapGet("/api/users/search", async (string q, UsersDbContext db) =>
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.BadRequest(new { error = "Query parameter 'q' is required" });
        }

        var profiles = await db.UserProfiles
            .Where(p => p.DisplayName.ToLower().Contains(q.ToLower()))
            .Take(20)
            .ToListAsync();

        return Results.Ok(profiles);
    }).RequireAuthorization();

    Log.Information("Users API starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Users API failed to start");
}
finally
{
    Log.CloseAndFlush();
}

// Database Context
public class UsersDbContext : DbContext
{
    public UsersDbContext(DbContextOptions<UsersDbContext> options) : base(options) { }

    public DbSet<UserProfile> UserProfiles { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId).IsUnique();
            entity.HasIndex(e => e.DisplayName);
        });
    }
}

// Models
public class UserProfile
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public record UserProfileRequest(string DisplayName, string? Bio, string? AvatarUrl);
