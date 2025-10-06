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
    builder.Services.AddDbContext<ExercisesDbContext>(options =>
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
        var db = scope.ServiceProvider.GetRequiredService<ExercisesDbContext>();
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
    app.MapGet("/api/exercises/health", () => Results.Ok(new { status = "Healthy", service = "Exercises API" }));

    // Get all exercises
    app.MapGet("/api/exercises", async (ExercisesDbContext db, string? category, string? muscleGroup, int? limit, int? offset) =>
    {
        var query = db.Exercises.AsQueryable();

        if (!string.IsNullOrEmpty(category))
        {
            query = query.Where(e => e.Category.ToLower() == category.ToLower());
        }

        if (!string.IsNullOrEmpty(muscleGroup))
        {
            query = query.Where(e => e.MuscleGroup.ToLower() == muscleGroup.ToLower());
        }

        var exercises = await query
            .OrderBy(e => e.Name)
            .Skip(offset ?? 0)
            .Take(limit ?? 50)
            .ToListAsync();

        return Results.Ok(exercises);
    }).RequireAuthorization();

    // Get single exercise
    app.MapGet("/api/exercises/{id:guid}", async (Guid id, ExercisesDbContext db) =>
    {
        var exercise = await db.Exercises.FindAsync(id);

        if (exercise == null)
        {
            return Results.NotFound(new { error = "Exercise not found" });
        }

        return Results.Ok(exercise);
    }).RequireAuthorization();

    // Create exercise
    app.MapPost("/api/exercises", async (HttpContext context, CreateExerciseRequest request, ExercisesDbContext db) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var exercise = new Exercise
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description ?? string.Empty,
            Category = request.Category ?? string.Empty,
            MediaUrl = request.MediaUrl ?? string.Empty,
            MuscleGroup = request.MuscleGroup ?? string.Empty,
            CreatedAt = DateTime.UtcNow
        };

        db.Exercises.Add(exercise);
        await db.SaveChangesAsync();

        Log.Information("Exercise created: {ExerciseId} by user {UserId}", exercise.Id, userId);

        return Results.Ok(exercise);
    }).RequireAuthorization();

    // Update exercise
    app.MapPut("/api/exercises/{id:guid}", async (Guid id, UpdateExerciseRequest request, ExercisesDbContext db) =>
    {
        var exercise = await db.Exercises.FindAsync(id);

        if (exercise == null)
        {
            return Results.NotFound(new { error = "Exercise not found" });
        }

        if (!string.IsNullOrEmpty(request.Name))
            exercise.Name = request.Name;
        if (request.Description != null)
            exercise.Description = request.Description;
        if (request.Category != null)
            exercise.Category = request.Category;
        if (request.MediaUrl != null)
            exercise.MediaUrl = request.MediaUrl;
        if (request.MuscleGroup != null)
            exercise.MuscleGroup = request.MuscleGroup;

        await db.SaveChangesAsync();

        Log.Information("Exercise updated: {ExerciseId}", exercise.Id);

        return Results.Ok(exercise);
    }).RequireAuthorization();

    // Delete exercise
    app.MapDelete("/api/exercises/{id:guid}", async (Guid id, ExercisesDbContext db) =>
    {
        var exercise = await db.Exercises.FindAsync(id);

        if (exercise == null)
        {
            return Results.NotFound(new { error = "Exercise not found" });
        }

        db.Exercises.Remove(exercise);
        await db.SaveChangesAsync();

        Log.Information("Exercise deleted: {ExerciseId}", exercise.Id);

        return Results.Ok(new { message = "Exercise deleted successfully" });
    }).RequireAuthorization();

    Log.Information("Exercises API starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Exercises API failed to start");
}
finally
{
    Log.CloseAndFlush();
}

// Database Context
public class ExercisesDbContext : DbContext
{
    public ExercisesDbContext(DbContextOptions<ExercisesDbContext> options) : base(options) { }

    public DbSet<Exercise> Exercises { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Exercise>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.MuscleGroup);
        });
    }
}

// Models
public class Exercise
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string MediaUrl { get; set; } = string.Empty;
    public string MuscleGroup { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public record CreateExerciseRequest(string Name, string? Description, string? Category, string? MediaUrl, string? MuscleGroup);
public record UpdateExerciseRequest(string? Name, string? Description, string? Category, string? MediaUrl, string? MuscleGroup);
