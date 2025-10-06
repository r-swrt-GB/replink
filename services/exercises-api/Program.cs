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
                // Create unique constraint on Exercise.id
                await tx.RunAsync("CREATE CONSTRAINT exercise_id_unique IF NOT EXISTS FOR (e:Exercise) REQUIRE e.id IS UNIQUE");

                // Create index on Exercise.name for faster searches
                await tx.RunAsync("CREATE INDEX exercise_name_index IF NOT EXISTS FOR (e:Exercise) ON (e.name)");

                // Create index on Exercise.category for filtering
                await tx.RunAsync("CREATE INDEX exercise_category_index IF NOT EXISTS FOR (e:Exercise) ON (e.category)");

                // Create index on Exercise.muscleGroup for filtering
                await tx.RunAsync("CREATE INDEX exercise_muscle_group_index IF NOT EXISTS FOR (e:Exercise) ON (e.muscleGroup)");
            });
            Log.Information("Neo4j constraints and indexes created for Exercises");
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
    app.MapGet("/api/exercises/health", () => Results.Ok(new { status = "Healthy", service = "Exercises API (Neo4j)" }));

    // Get all exercises
    app.MapGet("/api/exercises", async (IDriver driver, string? category, string? muscleGroup, int? limit, int? offset) =>
    {
        await using var session = driver.AsyncSession();
        var exercises = await session.ExecuteReadAsync(async tx =>
        {
            // Build WHERE clause dynamically based on filters
            var whereClauses = new List<string>();
            var parameters = new Dictionary<string, object>
            {
                { "offset", offset ?? 0 },
                { "limit", limit ?? 50 }
            };

            if (!string.IsNullOrEmpty(category))
            {
                whereClauses.Add("toLower(e.category) = toLower($category)");
                parameters["category"] = category;
            }

            if (!string.IsNullOrEmpty(muscleGroup))
            {
                whereClauses.Add("toLower(e.muscleGroup) = toLower($muscleGroup)");
                parameters["muscleGroup"] = muscleGroup;
            }

            var whereClause = whereClauses.Count > 0
                ? "WHERE " + string.Join(" AND ", whereClauses)
                : "";

            var query = $@"
                MATCH (e:Exercise)
                {whereClause}
                RETURN e.id AS id,
                       e.name AS name,
                       e.description AS description,
                       e.category AS category,
                       e.mediaUrl AS mediaUrl,
                       e.muscleGroup AS muscleGroup,
                       e.createdAt AS createdAt
                ORDER BY e.name
                SKIP $offset
                LIMIT $limit
            ";

            var cursor = await tx.RunAsync(query, parameters);
            var records = await cursor.ToListAsync();

            return records.Select(record => new Exercise
            {
                Id = record["id"].As<string>(),
                Name = record["name"].As<string>(),
                Description = record["description"].As<string>(),
                Category = record["category"].As<string>(),
                MediaUrl = record["mediaUrl"].As<string>(),
                MuscleGroup = record["muscleGroup"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            }).ToList();
        });

        return Results.Ok(exercises);
    }).RequireAuthorization();

    // Get single exercise
    app.MapGet("/api/exercises/{id}", async (string id, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();
        var exercise = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (e:Exercise {id: $id})
                RETURN e.id AS id,
                       e.name AS name,
                       e.description AS description,
                       e.category AS category,
                       e.mediaUrl AS mediaUrl,
                       e.muscleGroup AS muscleGroup,
                       e.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, new { id });
            var records = await cursor.ToListAsync();

            if (records.Count == 0)
                return null;

            var record = records[0];
            return new Exercise
            {
                Id = record["id"].As<string>(),
                Name = record["name"].As<string>(),
                Description = record["description"].As<string>(),
                Category = record["category"].As<string>(),
                MediaUrl = record["mediaUrl"].As<string>(),
                MuscleGroup = record["muscleGroup"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            };
        });

        if (exercise == null)
        {
            return Results.NotFound(new { error = "Exercise not found" });
        }

        return Results.Ok(exercise);
    }).RequireAuthorization();

    // Create exercise
    app.MapPost("/api/exercises", async (HttpContext context, CreateExerciseRequest request, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var exerciseId = Guid.NewGuid().ToString();
        var createdAt = DateTime.UtcNow;

        await using var session = driver.AsyncSession();
        var exercise = await session.ExecuteWriteAsync(async tx =>
        {
            var query = @"
                CREATE (e:Exercise {
                    id: $id,
                    name: $name,
                    description: $description,
                    category: $category,
                    mediaUrl: $mediaUrl,
                    muscleGroup: $muscleGroup,
                    createdAt: datetime($createdAt),
                    createdBy: $createdBy
                })
                RETURN e.id AS id,
                       e.name AS name,
                       e.description AS description,
                       e.category AS category,
                       e.mediaUrl AS mediaUrl,
                       e.muscleGroup AS muscleGroup,
                       e.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, new
            {
                id = exerciseId,
                name = request.Name,
                description = request.Description ?? string.Empty,
                category = request.Category ?? string.Empty,
                mediaUrl = request.MediaUrl ?? string.Empty,
                muscleGroup = request.MuscleGroup ?? string.Empty,
                createdAt = createdAt.ToString("o"),
                createdBy = userId
            });

            var record = await cursor.SingleAsync();

            return new Exercise
            {
                Id = record["id"].As<string>(),
                Name = record["name"].As<string>(),
                Description = record["description"].As<string>(),
                Category = record["category"].As<string>(),
                MediaUrl = record["mediaUrl"].As<string>(),
                MuscleGroup = record["muscleGroup"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            };
        });

        Log.Information("Exercise created: {ExerciseId} by user {UserId}", exerciseId, userId);

        return Results.Ok(exercise);
    }).RequireAuthorization();

    // Update exercise
    app.MapPut("/api/exercises/{id}", async (string id, UpdateExerciseRequest request, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();

        var exercise = await session.ExecuteWriteAsync(async tx =>
        {
            // Build dynamic SET clause based on provided fields
            var setClauses = new List<string>();
            var parameters = new Dictionary<string, object> { { "id", id } };

            if (!string.IsNullOrEmpty(request.Name))
            {
                setClauses.Add("e.name = $name");
                parameters["name"] = request.Name;
            }
            if (request.Description != null)
            {
                setClauses.Add("e.description = $description");
                parameters["description"] = request.Description;
            }
            if (request.Category != null)
            {
                setClauses.Add("e.category = $category");
                parameters["category"] = request.Category;
            }
            if (request.MediaUrl != null)
            {
                setClauses.Add("e.mediaUrl = $mediaUrl");
                parameters["mediaUrl"] = request.MediaUrl;
            }
            if (request.MuscleGroup != null)
            {
                setClauses.Add("e.muscleGroup = $muscleGroup");
                parameters["muscleGroup"] = request.MuscleGroup;
            }

            if (setClauses.Count == 0)
            {
                // No updates, just return existing exercise
                var getQuery = @"
                    MATCH (e:Exercise {id: $id})
                    RETURN e.id AS id,
                           e.name AS name,
                           e.description AS description,
                           e.category AS category,
                           e.mediaUrl AS mediaUrl,
                           e.muscleGroup AS muscleGroup,
                           e.createdAt AS createdAt
                ";
                var getCursor = await tx.RunAsync(getQuery, new { id });
                var getRecords = await getCursor.ToListAsync();

                if (getRecords.Count == 0)
                    return null;

                var getRecord = getRecords[0];
                return new Exercise
                {
                    Id = getRecord["id"].As<string>(),
                    Name = getRecord["name"].As<string>(),
                    Description = getRecord["description"].As<string>(),
                    Category = getRecord["category"].As<string>(),
                    MediaUrl = getRecord["mediaUrl"].As<string>(),
                    MuscleGroup = getRecord["muscleGroup"].As<string>(),
                    CreatedAt = getRecord["createdAt"].As<DateTime>()
                };
            }

            var query = $@"
                MATCH (e:Exercise {{id: $id}})
                SET {string.Join(", ", setClauses)}
                RETURN e.id AS id,
                       e.name AS name,
                       e.description AS description,
                       e.category AS category,
                       e.mediaUrl AS mediaUrl,
                       e.muscleGroup AS muscleGroup,
                       e.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, parameters);
            var records = await cursor.ToListAsync();

            if (records.Count == 0)
                return null;

            var record = records[0];
            return new Exercise
            {
                Id = record["id"].As<string>(),
                Name = record["name"].As<string>(),
                Description = record["description"].As<string>(),
                Category = record["category"].As<string>(),
                MediaUrl = record["mediaUrl"].As<string>(),
                MuscleGroup = record["muscleGroup"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            };
        });

        if (exercise == null)
        {
            return Results.NotFound(new { error = "Exercise not found" });
        }

        Log.Information("Exercise updated: {ExerciseId}", id);

        return Results.Ok(exercise);
    }).RequireAuthorization();

    // Delete exercise
    app.MapDelete("/api/exercises/{id}", async (string id, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();

        var deleted = await session.ExecuteWriteAsync(async tx =>
        {
            // First check if exercise exists
            var checkQuery = "MATCH (e:Exercise {id: $id}) RETURN e";
            var checkCursor = await tx.RunAsync(checkQuery, new { id });
            var exists = await checkCursor.FetchAsync();

            if (!exists)
                return false;

            // Delete the exercise and all its relationships
            var deleteQuery = @"
                MATCH (e:Exercise {id: $id})
                DETACH DELETE e
                RETURN count(e) AS deleted
            ";

            var cursor = await tx.RunAsync(deleteQuery, new { id });
            var record = await cursor.SingleAsync();
            return record["deleted"].As<int>() > 0;
        });

        if (!deleted)
        {
            return Results.NotFound(new { error = "Exercise not found" });
        }

        Log.Information("Exercise deleted: {ExerciseId}", id);

        return Results.Ok(new { message = "Exercise deleted successfully" });
    }).RequireAuthorization();

    Log.Information("Exercises API (Neo4j) starting on port 80");

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

// Models
public class Exercise
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string MediaUrl { get; set; } = string.Empty;
    public string MuscleGroup { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public record CreateExerciseRequest(string Name, string? Description, string? Category, string? MediaUrl, string? MuscleGroup);
public record UpdateExerciseRequest(string? Name, string? Description, string? Category, string? MediaUrl, string? MuscleGroup);
