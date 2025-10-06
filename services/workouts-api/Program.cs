using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Text;
using System.Security.Claims;
using Neo4j.Driver;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // Neo4j Configuration (reuse existing connection from social-graph)
    var neo4jUri = builder.Configuration["Neo4j:Uri"] ?? "bolt://neo4j:7687";
    var neo4jUser = builder.Configuration["Neo4j:User"] ?? "neo4j";
    var neo4jPassword = builder.Configuration["Neo4j:Password"] ?? "replinkneo4j";

    builder.Services.AddSingleton(GraphDatabase.Driver(neo4jUri, AuthTokens.Basic(neo4jUser, neo4jPassword)));

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

    // Create Neo4j constraints on startup
    using (var scope = app.Services.CreateScope())
    {
        var driver = scope.ServiceProvider.GetRequiredService<IDriver>();
        await using var session = driver.AsyncSession();
        try
        {
            await session.ExecuteWriteAsync(async tx =>
            {
                // Create unique constraint on Workout.id
                await tx.RunAsync("CREATE CONSTRAINT workout_id_unique IF NOT EXISTS FOR (w:Workout) REQUIRE w.id IS UNIQUE");

                // Create index on Workout.userId for faster user queries
                await tx.RunAsync("CREATE INDEX workout_userId_index IF NOT EXISTS FOR (w:Workout) ON (w.userId)");

                // Create index on Workout.createdAt for ordering
                await tx.RunAsync("CREATE INDEX workout_createdAt_index IF NOT EXISTS FOR (w:Workout) ON (w.createdAt)");

                // Create index on Workout.title for searches
                await tx.RunAsync("CREATE INDEX workout_title_index IF NOT EXISTS FOR (w:Workout) ON (w.title)");
            });
            Log.Information("Neo4j constraints and indexes created for Workouts");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create Neo4j constraints (may already exist)");
        }
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseAuthentication();
    app.UseAuthorization();

    // Health endpoint
    app.MapGet("/api/workouts/health", () => Results.Ok(new { status = "Healthy", service = "Workouts API (Neo4j)" }));

    // Get all workouts
    app.MapGet("/api/workouts", async (IDriver driver, int? limit, int? offset) =>
    {
        await using var session = driver.AsyncSession();
        var workouts = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (w:Workout)
                RETURN w.id AS id,
                       w.userId AS userId,
                       w.title AS title,
                       w.description AS description,
                       w.exerciseIds AS exerciseIds,
                       w.durationMinutes AS durationMinutes,
                       w.createdAt AS createdAt
                ORDER BY w.createdAt DESC
                SKIP $offset
                LIMIT $limit
            ";

            var cursor = await tx.RunAsync(query, new {
                offset = offset ?? 0,
                limit = limit ?? 20
            });

            var records = await cursor.ToListAsync();
            return records.Select(record => new Workout
            {
                Id = record["id"].As<string>(),
                UserId = record["userId"].As<string>(),
                Title = record["title"].As<string>(),
                Description = record["description"].As<string>(),
                ExerciseIds = record["exerciseIds"].As<List<string>>().ToArray(),
                DurationMinutes = record["durationMinutes"].As<int>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            }).ToList();
        });

        return Results.Ok(workouts);
    }).RequireAuthorization();

    // Get single workout
    app.MapGet("/api/workouts/{id}", async (string id, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();
        var workout = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (w:Workout {id: $id})
                RETURN w.id AS id,
                       w.userId AS userId,
                       w.title AS title,
                       w.description AS description,
                       w.exerciseIds AS exerciseIds,
                       w.durationMinutes AS durationMinutes,
                       w.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, new { id });
            var records = await cursor.ToListAsync();

            if (records.Count == 0)
                return null;

            var record = records[0];
            return new Workout
            {
                Id = record["id"].As<string>(),
                UserId = record["userId"].As<string>(),
                Title = record["title"].As<string>(),
                Description = record["description"].As<string>(),
                ExerciseIds = record["exerciseIds"].As<List<string>>().ToArray(),
                DurationMinutes = record["durationMinutes"].As<int>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            };
        });

        if (workout == null)
        {
            return Results.NotFound(new { error = "Workout not found" });
        }

        return Results.Ok(workout);
    }).RequireAuthorization();

    // Get user's workouts
    app.MapGet("/api/workouts/user/{userId}", async (string userId, IDriver driver, int? limit, int? offset) =>
    {
        await using var session = driver.AsyncSession();
        var workouts = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (w:Workout {userId: $userId})
                RETURN w.id AS id,
                       w.userId AS userId,
                       w.title AS title,
                       w.description AS description,
                       w.exerciseIds AS exerciseIds,
                       w.durationMinutes AS durationMinutes,
                       w.createdAt AS createdAt
                ORDER BY w.createdAt DESC
                SKIP $offset
                LIMIT $limit
            ";

            var cursor = await tx.RunAsync(query, new {
                userId,
                offset = offset ?? 0,
                limit = limit ?? 20
            });

            var records = await cursor.ToListAsync();
            return records.Select(record => new Workout
            {
                Id = record["id"].As<string>(),
                UserId = record["userId"].As<string>(),
                Title = record["title"].As<string>(),
                Description = record["description"].As<string>(),
                ExerciseIds = record["exerciseIds"].As<List<string>>().ToArray(),
                DurationMinutes = record["durationMinutes"].As<int>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            }).ToList();
        });

        return Results.Ok(workouts);
    }).RequireAuthorization();

    // Create workout
    app.MapPost("/api/workouts", async (HttpContext context, CreateWorkoutRequest request, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var workoutId = Guid.NewGuid().ToString();
        var createdAt = DateTime.UtcNow;

        await using var session = driver.AsyncSession();
        var workout = await session.ExecuteWriteAsync(async tx =>
        {
            var query = @"
                CREATE (w:Workout {
                    id: $id,
                    userId: $userId,
                    title: $title,
                    description: $description,
                    exerciseIds: $exerciseIds,
                    durationMinutes: $durationMinutes,
                    createdAt: datetime($createdAt)
                })
                RETURN w.id AS id,
                       w.userId AS userId,
                       w.title AS title,
                       w.description AS description,
                       w.exerciseIds AS exerciseIds,
                       w.durationMinutes AS durationMinutes,
                       w.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, new
            {
                id = workoutId,
                userId,
                title = request.Title,
                description = request.Description ?? string.Empty,
                exerciseIds = request.ExerciseIds ?? Array.Empty<string>(),
                durationMinutes = request.DurationMinutes ?? 0,
                createdAt = createdAt.ToString("o")
            });

            var record = await cursor.SingleAsync();

            return new Workout
            {
                Id = record["id"].As<string>(),
                UserId = record["userId"].As<string>(),
                Title = record["title"].As<string>(),
                Description = record["description"].As<string>(),
                ExerciseIds = record["exerciseIds"].As<List<string>>().ToArray(),
                DurationMinutes = record["durationMinutes"].As<int>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            };
        });

        Log.Information("Workout created: {WorkoutId} by user {UserId}", workoutId, userId);

        return Results.Ok(workout);
    }).RequireAuthorization();

    // Update workout
    app.MapPut("/api/workouts/{id}", async (string id, HttpContext context, UpdateWorkoutRequest request, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        await using var session = driver.AsyncSession();

        var workout = await session.ExecuteWriteAsync(async tx =>
        {
            // First check if workout exists and user owns it
            var checkQuery = @"
                MATCH (w:Workout {id: $id})
                RETURN w.userId AS userId
            ";
            var checkCursor = await tx.RunAsync(checkQuery, new { id });
            var checkRecords = await checkCursor.ToListAsync();

            if (checkRecords.Count == 0)
                return null;

            var workoutUserId = checkRecords[0]["userId"].As<string>();
            if (workoutUserId != userId)
            {
                throw new UnauthorizedAccessException("User does not own this workout");
            }

            // Build dynamic SET clause based on provided fields
            var setClauses = new List<string>();
            var parameters = new Dictionary<string, object> { { "id", id } };

            if (!string.IsNullOrEmpty(request.Title))
            {
                setClauses.Add("w.title = $title");
                parameters["title"] = request.Title;
            }
            if (request.Description != null)
            {
                setClauses.Add("w.description = $description");
                parameters["description"] = request.Description;
            }
            if (request.ExerciseIds != null)
            {
                setClauses.Add("w.exerciseIds = $exerciseIds");
                parameters["exerciseIds"] = request.ExerciseIds;
            }
            if (request.DurationMinutes.HasValue)
            {
                setClauses.Add("w.durationMinutes = $durationMinutes");
                parameters["durationMinutes"] = request.DurationMinutes.Value;
            }

            if (setClauses.Count == 0)
            {
                // No updates, just return existing workout
                var getQuery = @"
                    MATCH (w:Workout {id: $id})
                    RETURN w.id AS id,
                           w.userId AS userId,
                           w.title AS title,
                           w.description AS description,
                           w.exerciseIds AS exerciseIds,
                           w.durationMinutes AS durationMinutes,
                           w.createdAt AS createdAt
                ";
                var getCursor = await tx.RunAsync(getQuery, new { id });
                var getRecords = await getCursor.ToListAsync();

                if (getRecords.Count == 0)
                    return null;

                var getRecord = getRecords[0];
                return new Workout
                {
                    Id = getRecord["id"].As<string>(),
                    UserId = getRecord["userId"].As<string>(),
                    Title = getRecord["title"].As<string>(),
                    Description = getRecord["description"].As<string>(),
                    ExerciseIds = getRecord["exerciseIds"].As<List<string>>().ToArray(),
                    DurationMinutes = getRecord["durationMinutes"].As<int>(),
                    CreatedAt = getRecord["createdAt"].As<DateTime>()
                };
            }

            var query = $@"
                MATCH (w:Workout {{id: $id}})
                SET {string.Join(", ", setClauses)}
                RETURN w.id AS id,
                       w.userId AS userId,
                       w.title AS title,
                       w.description AS description,
                       w.exerciseIds AS exerciseIds,
                       w.durationMinutes AS durationMinutes,
                       w.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, parameters);
            var records = await cursor.ToListAsync();

            if (records.Count == 0)
                return null;

            var record = records[0];
            return new Workout
            {
                Id = record["id"].As<string>(),
                UserId = record["userId"].As<string>(),
                Title = record["title"].As<string>(),
                Description = record["description"].As<string>(),
                ExerciseIds = record["exerciseIds"].As<List<string>>().ToArray(),
                DurationMinutes = record["durationMinutes"].As<int>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            };
        });

        if (workout == null)
        {
            return Results.NotFound(new { error = "Workout not found" });
        }

        Log.Information("Workout updated: {WorkoutId}", id);

        return Results.Ok(workout);
    }).RequireAuthorization();

    // Delete workout
    app.MapDelete("/api/workouts/{id}", async (string id, HttpContext context, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        await using var session = driver.AsyncSession();

        var result = await session.ExecuteWriteAsync(async tx =>
        {
            // First check if workout exists and user owns it
            var checkQuery = @"
                MATCH (w:Workout {id: $id})
                RETURN w.userId AS userId
            ";
            var checkCursor = await tx.RunAsync(checkQuery, new { id });
            var checkRecords = await checkCursor.ToListAsync();

            if (checkRecords.Count == 0)
                return (false, false); // (exists, authorized)

            var workoutUserId = checkRecords[0]["userId"].As<string>();
            if (workoutUserId != userId)
            {
                return (true, false); // exists but not authorized
            }

            // Delete the workout and all its relationships
            var deleteQuery = @"
                MATCH (w:Workout {id: $id})
                DETACH DELETE w
                RETURN count(w) AS deleted
            ";

            var cursor = await tx.RunAsync(deleteQuery, new { id });
            var record = await cursor.SingleAsync();
            return (true, record["deleted"].As<int>() > 0); // (authorized, deleted)
        });

        if (!result.Item1)
        {
            return Results.NotFound(new { error = "Workout not found" });
        }

        if (!result.Item2)
        {
            return Results.Forbid();
        }

        Log.Information("Workout deleted: {WorkoutId}", id);

        return Results.Ok(new { message = "Workout deleted successfully" });
    }).RequireAuthorization();

    Log.Information("Workouts API (Neo4j) starting on port 80");

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

// Models
public class Workout
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string[] ExerciseIds { get; set; } = Array.Empty<string>();
    public int DurationMinutes { get; set; }
    public DateTime CreatedAt { get; set; }
}

public record CreateWorkoutRequest(string Title, string? Description, string[]? ExerciseIds, int? DurationMinutes);
public record UpdateWorkoutRequest(string? Title, string? Description, string[]? ExerciseIds, int? DurationMinutes);
