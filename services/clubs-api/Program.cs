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
    builder.Services.AddDbContext<ClubsDbContext>(options =>
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
        var db = scope.ServiceProvider.GetRequiredService<ClubsDbContext>();
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
    app.MapGet("/api/clubs/health", () => Results.Ok(new { status = "Healthy", service = "Clubs API" }));

    // Get all clubs
    app.MapGet("/api/clubs", async (ClubsDbContext db, int? limit, int? offset) =>
    {
        var clubs = await db.Clubs
            .OrderBy(c => c.Name)
            .Skip(offset ?? 0)
            .Take(limit ?? 50)
            .ToListAsync();

        return Results.Ok(clubs);
    }).RequireAuthorization();

    // Get single club
    app.MapGet("/api/clubs/{id:guid}", async (Guid id, ClubsDbContext db) =>
    {
        var club = await db.Clubs.FindAsync(id);

        if (club == null)
        {
            return Results.NotFound(new { error = "Club not found" });
        }

        return Results.Ok(club);
    }).RequireAuthorization();

    // Create club
    app.MapPost("/api/clubs", async (HttpContext context, CreateClubRequest request, ClubsDbContext db) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var club = new Club
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description ?? string.Empty,
            Location = request.Location ?? string.Empty,
            CreatedAt = DateTime.UtcNow
        };

        db.Clubs.Add(club);
        await db.SaveChangesAsync();

        Log.Information("Club created: {ClubId} by user {UserId}", club.Id, userId);

        return Results.Ok(club);
    }).RequireAuthorization();

    // Update club
    app.MapPut("/api/clubs/{id:guid}", async (Guid id, UpdateClubRequest request, ClubsDbContext db) =>
    {
        var club = await db.Clubs.FindAsync(id);

        if (club == null)
        {
            return Results.NotFound(new { error = "Club not found" });
        }

        if (!string.IsNullOrEmpty(request.Name))
            club.Name = request.Name;
        if (request.Description != null)
            club.Description = request.Description;
        if (request.Location != null)
            club.Location = request.Location;

        await db.SaveChangesAsync();

        Log.Information("Club updated: {ClubId}", club.Id);

        return Results.Ok(club);
    }).RequireAuthorization();

    // Delete club
    app.MapDelete("/api/clubs/{id:guid}", async (Guid id, ClubsDbContext db) =>
    {
        var club = await db.Clubs.FindAsync(id);

        if (club == null)
        {
            return Results.NotFound(new { error = "Club not found" });
        }

        db.Clubs.Remove(club);
        await db.SaveChangesAsync();

        Log.Information("Club deleted: {ClubId}", club.Id);

        return Results.Ok(new { message = "Club deleted successfully" });
    }).RequireAuthorization();

    Log.Information("Clubs API starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Clubs API failed to start");
}
finally
{
    Log.CloseAndFlush();
}

// Database Context
public class ClubsDbContext : DbContext
{
    public ClubsDbContext(DbContextOptions<ClubsDbContext> options) : base(options) { }

    public DbSet<Club> Clubs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Club>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.Location);
        });
    }
}

// Models
public class Club
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public record CreateClubRequest(string Name, string? Description, string? Location);
public record UpdateClubRequest(string? Name, string? Description, string? Location);
