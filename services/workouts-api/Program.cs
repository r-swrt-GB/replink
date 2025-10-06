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
    builder.Services.AddDbContext<WorkoutsDbContext>(options =>
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
        var db = scope.ServiceProvider.GetRequiredService<WorkoutsDbContext>();
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
    app.MapGet("/api/workouts/health", () => Results.Ok(new { status = "Healthy", service = "Workouts API" }));

    // Get all workouts
    app.MapGet("/api/workouts", async (HttpContext context, WorkoutsDbContext db, int? limit, int? offset) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        var workouts = await db.Workouts
            .OrderByDescending(w => w.CreatedAt)
            .Skip(offset ?? 0)
            .Take(limit ?? 20)
            .ToListAsync();

        return Results.Ok(workouts);
    }).RequireAuthorization();

    // Get single workout
    app.MapGet("/api/workouts/{id:guid}", async (Guid id, WorkoutsDbContext db) =>
    {
        var workout = await db.Workouts.FindAsync(id);

        if (workout == null)
        {
            return Results.NotFound(new { error = "Workout not found" });
        }

        return Results.Ok(workout);
    }).RequireAuthorization();

    // Get user's workouts
    app.MapGet("/api/workouts/user/{userId:guid}", async (Guid userId, WorkoutsDbContext db, int? limit, int? offset) =>
    {
        var workouts = await db.Workouts
            .Where(w => w.UserId == userId)
            .OrderByDescending(w => w.CreatedAt)
            .Skip(offset ?? 0)
            .Take(limit ?? 20)
            .ToListAsync();

        return Results.Ok(workouts);
    }).RequireAuthorization();

    // Create workout
    app.MapPost("/api/workouts", async (HttpContext context, CreateWorkoutRequest request, WorkoutsDbContext db) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var workout = new Workout
        {
            Id = Guid.NewGuid(),
            UserId = Guid.Parse(userId),
            Title = request.Title,
            Description = request.Description ?? string.Empty,
            ExerciseIds = request.ExerciseIds ?? Array.Empty<Guid>(),
            DurationMinutes = request.DurationMinutes ?? 0,
            CreatedAt = DateTime.UtcNow
        };

        db.Workouts.Add(workout);
        await db.SaveChangesAsync();

        Log.Information("Workout created: {WorkoutId} by user {UserId}", workout.Id, userId);

        return Results.Ok(workout);
    }).RequireAuthorization();

    // Update workout
    app.MapPut("/api/workouts/{id:guid}", async (Guid id, HttpContext context, UpdateWorkoutRequest request, WorkoutsDbContext db) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var workout = await db.Workouts.FindAsync(id);

        if (workout == null)
        {
            return Results.NotFound(new { error = "Workout not found" });
        }

        // Check if user owns the workout
        if (workout.UserId.ToString() != userId)
        {
            return Results.Forbid();
        }

        if (!string.IsNullOrEmpty(request.Title))
            workout.Title = request.Title;
        if (request.Description != null)
            workout.Description = request.Description;
        if (request.ExerciseIds != null)
            workout.ExerciseIds = request.ExerciseIds;
        if (request.DurationMinutes.HasValue)
            workout.DurationMinutes = request.DurationMinutes.Value;

        await db.SaveChangesAsync();

        Log.Information("Workout updated: {WorkoutId}", workout.Id);

        return Results.Ok(workout);
    }).RequireAuthorization();

    // Delete workout
    app.MapDelete("/api/workouts/{id:guid}", async (Guid id, HttpContext context, WorkoutsDbContext db) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var workout = await db.Workouts.FindAsync(id);

        if (workout == null)
        {
            return Results.NotFound(new { error = "Workout not found" });
        }

        // Check if user owns the workout
        if (workout.UserId.ToString() != userId)
        {
            return Results.Forbid();
        }

        db.Workouts.Remove(workout);
        await db.SaveChangesAsync();

        Log.Information("Workout deleted: {WorkoutId}", workout.Id);

        return Results.Ok(new { message = "Workout deleted successfully" });
    }).RequireAuthorization();

    Log.Information("Workouts API starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Workouts API failed to start");
}
finally
{
    Log.CloseAndFlush();
}

// Database Context
public class WorkoutsDbContext : DbContext
{
    public WorkoutsDbContext(DbContextOptions<WorkoutsDbContext> options) : base(options) { }

    public DbSet<Workout> Workouts { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Workout>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CreatedAt);
        });
    }
}

// Models
public class Workout
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid[] ExerciseIds { get; set; } = Array.Empty<Guid>();
    public int DurationMinutes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public record CreateWorkoutRequest(string Title, string? Description, Guid[]? ExerciseIds, int? DurationMinutes);
public record UpdateWorkoutRequest(string? Title, string? Description, Guid[]? ExerciseIds, int? DurationMinutes);
