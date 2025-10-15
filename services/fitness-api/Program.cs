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

    // Neo4j Configuration
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
                // Workout constraints and indexes
                await tx.RunAsync("CREATE CONSTRAINT workout_id_unique IF NOT EXISTS FOR (w:Workout) REQUIRE w.id IS UNIQUE");
                await tx.RunAsync("CREATE INDEX workout_userId_index IF NOT EXISTS FOR (w:Workout) ON (w.userId)");
                await tx.RunAsync("CREATE INDEX workout_createdAt_index IF NOT EXISTS FOR (w:Workout) ON (w.createdAt)");
                await tx.RunAsync("CREATE INDEX workout_title_index IF NOT EXISTS FOR (w:Workout) ON (w.title)");

                // Exercise constraints and indexes
                await tx.RunAsync("CREATE CONSTRAINT exercise_id_unique IF NOT EXISTS FOR (e:Exercise) REQUIRE e.id IS UNIQUE");
                await tx.RunAsync("CREATE INDEX exercise_name_index IF NOT EXISTS FOR (e:Exercise) ON (e.name)");
                await tx.RunAsync("CREATE INDEX exercise_category_index IF NOT EXISTS FOR (e:Exercise) ON (e.category)");
                await tx.RunAsync("CREATE INDEX exercise_muscle_group_index IF NOT EXISTS FOR (e:Exercise) ON (e.muscleGroup)");

                // Club constraints and indexes
                await tx.RunAsync("CREATE CONSTRAINT club_id_unique IF NOT EXISTS FOR (c:Club) REQUIRE c.id IS UNIQUE");
                await tx.RunAsync("CREATE INDEX club_name_index IF NOT EXISTS FOR (c:Club) ON (c.name)");
                await tx.RunAsync("CREATE INDEX club_location_index IF NOT EXISTS FOR (c:Club) ON (c.location)");
            });
            Log.Information("Neo4j constraints and indexes created for Fitness API (Workouts, Exercises, Clubs)");
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

    // Health endpoint with Neo4j connectivity check
    app.MapGet("/api/fitness/health", async (IDriver driver) =>
    {
        try
        {
            await using var session = driver.AsyncSession();
            await session.ExecuteReadAsync(async tx =>
            {
                await tx.RunAsync("RETURN 1");
            });

            return Results.Ok(new
            {
                status = "Healthy",
                service = "Fitness API",
                database = "Neo4j",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Health check failed for Fitness API");
            return Results.Problem(new
            {
                status = "Unhealthy",
                service = "Fitness API",
                error = ex.Message,
                timestamp = DateTime.UtcNow
            }.ToString());
        }
    });

    // ========== WORKOUT ENDPOINTS ==========

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
                ExerciseIds = ConvertToStringArray(record["exerciseIds"]),
                DurationMinutes = record["durationMinutes"].As<int>(),
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
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
                ExerciseIds = ConvertToStringArray(record["exerciseIds"]),
                DurationMinutes = record["durationMinutes"].As<int>(),
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
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
                ExerciseIds = ConvertToStringArray(record["exerciseIds"]),
                DurationMinutes = record["durationMinutes"].As<int>(),
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
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
                ExerciseIds = ConvertToStringArray(record["exerciseIds"]),
                DurationMinutes = record["durationMinutes"].As<int>(),
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
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

            // Build dynamic SET clause
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
                    ExerciseIds = ConvertToStringArray(getRecord["exerciseIds"]),
                    DurationMinutes = getRecord["durationMinutes"].As<int>(),
                    CreatedAt = ConvertToDateTime(getRecord["createdAt"].As<object>())
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
                ExerciseIds = ConvertToStringArray(record["exerciseIds"]),
                DurationMinutes = record["durationMinutes"].As<int>(),
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
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
            var checkQuery = @"
                MATCH (w:Workout {id: $id})
                RETURN w.userId AS userId
            ";
            var checkCursor = await tx.RunAsync(checkQuery, new { id });
            var checkRecords = await checkCursor.ToListAsync();

            if (checkRecords.Count == 0)
                return (false, false);

            var workoutUserId = checkRecords[0]["userId"].As<string>();
            if (workoutUserId != userId)
            {
                return (true, false);
            }

            var deleteQuery = @"
                MATCH (w:Workout {id: $id})
                DETACH DELETE w
                RETURN count(w) AS deleted
            ";

            var cursor = await tx.RunAsync(deleteQuery, new { id });
            var record = await cursor.SingleAsync();
            return (true, record["deleted"].As<int>() > 0);
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

    // ========== EXERCISE ENDPOINTS ==========

    // Get all exercises
    app.MapGet("/api/exercises", async (IDriver driver, string? category, string? muscleGroup, int? limit, int? offset) =>
    {
        await using var session = driver.AsyncSession();
        var exercises = await session.ExecuteReadAsync(async tx =>
        {
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
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
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
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
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
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
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
                    CreatedAt = ConvertToDateTime(getRecord["createdAt"].As<object>())
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
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
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
            var checkQuery = "MATCH (e:Exercise {id: $id}) RETURN e";
            var checkCursor = await tx.RunAsync(checkQuery, new { id });
            var exists = await checkCursor.FetchAsync();

            if (!exists)
                return false;

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

    // ========== CLUB ENDPOINTS ==========

    // Get all clubs
    app.MapGet("/api/clubs", async (IDriver driver, int? limit, int? offset) =>
    {
        await using var session = driver.AsyncSession();
        var clubs = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (c:Club)
                RETURN c.id AS id,
                       c.name AS name,
                       c.description AS description,
                       c.location AS location,
                       c.createdAt AS createdAt
                ORDER BY c.name
                SKIP $offset
                LIMIT $limit
            ";

            var cursor = await tx.RunAsync(query, new {
                offset = offset ?? 0,
                limit = limit ?? 50
            });

            var records = await cursor.ToListAsync();
            return records.Select(record => new Club
            {
                Id = record["id"].As<string>(),
                Name = record["name"].As<string>(),
                Description = record["description"].As<string>(),
                Location = record["location"].As<string>(),
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
            }).ToList();
        });

        return Results.Ok(clubs);
    }).RequireAuthorization();

    // Get single club
    app.MapGet("/api/clubs/{id}", async (string id, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();
        var club = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (c:Club {id: $id})
                RETURN c.id AS id,
                       c.name AS name,
                       c.description AS description,
                       c.location AS location,
                       c.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, new { id });
            var records = await cursor.ToListAsync();

            if (records.Count == 0)
                return null;

            var record = records[0];
            return new Club
            {
                Id = record["id"].As<string>(),
                Name = record["name"].As<string>(),
                Description = record["description"].As<string>(),
                Location = record["location"].As<string>(),
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
            };
        });

        if (club == null)
        {
            return Results.NotFound(new { error = "Club not found" });
        }

        return Results.Ok(club);
    }).RequireAuthorization();

    // Create club
    app.MapPost("/api/clubs", async (HttpContext context, CreateClubRequest request, IDriver driver) =>
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var clubId = Guid.NewGuid().ToString();
        var createdAt = DateTime.UtcNow;

        await using var session = driver.AsyncSession();
        var club = await session.ExecuteWriteAsync(async tx =>
        {
            var query = @"
                CREATE (c:Club {
                    id: $id,
                    name: $name,
                    description: $description,
                    location: $location,
                    createdAt: datetime($createdAt),
                    createdBy: $createdBy
                })
                RETURN c.id AS id,
                       c.name AS name,
                       c.description AS description,
                       c.location AS location,
                       c.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, new
            {
                id = clubId,
                name = request.Name,
                description = request.Description ?? string.Empty,
                location = request.Location ?? string.Empty,
                createdAt = createdAt.ToString("o"),
                createdBy = userId
            });

            var record = await cursor.SingleAsync();

            return new Club
            {
                Id = record["id"].As<string>(),
                Name = record["name"].As<string>(),
                Description = record["description"].As<string>(),
                Location = record["location"].As<string>(),
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
            };
        });

        Log.Information("Club created: {ClubId} by user {UserId}", clubId, userId);

        return Results.Ok(club);
    }).RequireAuthorization();

    // Update club
    app.MapPut("/api/clubs/{id}", async (string id, UpdateClubRequest request, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();

        var club = await session.ExecuteWriteAsync(async tx =>
        {
            var setClauses = new List<string>();
            var parameters = new Dictionary<string, object> { { "id", id } };

            if (!string.IsNullOrEmpty(request.Name))
            {
                setClauses.Add("c.name = $name");
                parameters["name"] = request.Name;
            }
            if (request.Description != null)
            {
                setClauses.Add("c.description = $description");
                parameters["description"] = request.Description;
            }
            if (request.Location != null)
            {
                setClauses.Add("c.location = $location");
                parameters["location"] = request.Location;
            }

            if (setClauses.Count == 0)
            {
                var getQuery = @"
                    MATCH (c:Club {id: $id})
                    RETURN c.id AS id,
                           c.name AS name,
                           c.description AS description,
                           c.location AS location,
                           c.createdAt AS createdAt
                ";
                var getCursor = await tx.RunAsync(getQuery, new { id });
                var getRecords = await getCursor.ToListAsync();

                if (getRecords.Count == 0)
                    return null;

                var getRecord = getRecords[0];
                return new Club
                {
                    Id = getRecord["id"].As<string>(),
                    Name = getRecord["name"].As<string>(),
                    Description = getRecord["description"].As<string>(),
                    Location = getRecord["location"].As<string>(),
                    CreatedAt = ConvertToDateTime(getRecord["createdAt"].As<object>())
                };
            }

            var query = $@"
                MATCH (c:Club {{id: $id}})
                SET {string.Join(", ", setClauses)}
                RETURN c.id AS id,
                       c.name AS name,
                       c.description AS description,
                       c.location AS location,
                       c.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, parameters);
            var records = await cursor.ToListAsync();

            if (records.Count == 0)
                return null;

            var record = records[0];
            return new Club
            {
                Id = record["id"].As<string>(),
                Name = record["name"].As<string>(),
                Description = record["description"].As<string>(),
                Location = record["location"].As<string>(),
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
            };
        });

        if (club == null)
        {
            return Results.NotFound(new { error = "Club not found" });
        }

        Log.Information("Club updated: {ClubId}", id);

        return Results.Ok(club);
    }).RequireAuthorization();

    // Delete club
    app.MapDelete("/api/clubs/{id}", async (string id, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();

        var deleted = await session.ExecuteWriteAsync(async tx =>
        {
            var checkQuery = "MATCH (c:Club {id: $id}) RETURN c";
            var checkCursor = await tx.RunAsync(checkQuery, new { id });
            var exists = await checkCursor.FetchAsync();

            if (!exists)
                return false;

            var deleteQuery = @"
                MATCH (c:Club {id: $id})
                DETACH DELETE c
                RETURN count(c) AS deleted
            ";

            var cursor = await tx.RunAsync(deleteQuery, new { id });
            var record = await cursor.SingleAsync();
            return record["deleted"].As<int>() > 0;
        });

        if (!deleted)
        {
            return Results.NotFound(new { error = "Club not found" });
        }

        Log.Information("Club deleted: {ClubId}", id);

        return Results.Ok(new { message = "Club deleted successfully" });
    }).RequireAuthorization();

    // Club search
    app.MapGet("/api/clubs/search", async (string? query, IDriver driver, int? limit) =>
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Results.BadRequest(new { error = "Search query is required" });
        }

        await using var session = driver.AsyncSession();
        var clubs = await session.ExecuteReadAsync(async tx =>
        {
            var cypherQuery = @"
                MATCH (c:Club)
                WHERE toLower(c.name) CONTAINS toLower($query)
                   OR toLower(c.location) CONTAINS toLower($query)
                RETURN c.id AS id,
                       c.name AS name,
                       c.description AS description,
                       c.location AS location,
                       c.createdAt AS createdAt
                ORDER BY c.name
                LIMIT $limit
            ";

            var cursor = await tx.RunAsync(cypherQuery, new {
                query,
                limit = limit ?? 20
            });

            var records = await cursor.ToListAsync();
            return records.Select(record => new Club
            {
                Id = record["id"].As<string>(),
                Name = record["name"].As<string>(),
                Description = record["description"].As<string>(),
                Location = record["location"].As<string>(),
                CreatedAt = ConvertToDateTime(record["createdAt"].As<object>())
            }).ToList();
        });

        return Results.Ok(clubs);
    }).RequireAuthorization();

    Log.Information("Fitness API (Neo4j - Workouts, Exercises, Clubs) starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Fitness API failed to start");
}
finally
{
    Log.CloseAndFlush();
}

// Helper method to safely convert Neo4j temporal values to DateTime
static DateTime ConvertToDateTime(object value)
{
    if (value == null)
        return DateTime.MinValue;

    // Handle Neo4j.Driver temporal types (ZonedDateTime, LocalDateTime)
    var valueType = value.GetType();

    // Check if it has ToDateTimeOffset() method (ZonedDateTime)
    var toDateTimeOffsetMethod = valueType.GetMethod("ToDateTimeOffset");
    if (toDateTimeOffsetMethod != null)
    {
        var dateTimeOffset = (DateTimeOffset)toDateTimeOffsetMethod.Invoke(value, null);
        return dateTimeOffset.DateTime;
    }

    // Check if it has ToDateTimeUnspecified() method (LocalDateTime)
    var toDateTimeUnspecifiedMethod = valueType.GetMethod("ToDateTimeUnspecified");
    if (toDateTimeUnspecifiedMethod != null)
    {
        return (DateTime)toDateTimeUnspecifiedMethod.Invoke(value, null);
    }

    if (value is DateTime dateTime)
        return dateTime;

    // Fallback: try to parse as string
    if (value is string str && DateTime.TryParse(str, out var parsed))
        return parsed;

    return DateTime.MinValue;
}

// Helper method to safely convert Neo4j values to string arrays
static string[] ConvertToStringArray(object value)
{
    if (value == null)
        return Array.Empty<string>();

    if (value is IList<object> list)
        return list.Select(item => item?.ToString() ?? string.Empty).ToArray();

    if (value is string[] stringArray)
        return stringArray;

    if (value is IEnumerable<string> stringEnumerable)
        return stringEnumerable.ToArray();

    return Array.Empty<string>();
}

// ========== MODELS ==========

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

public class Club
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public record CreateWorkoutRequest(string Title, string? Description, string[]? ExerciseIds, int? DurationMinutes);
public record UpdateWorkoutRequest(string? Title, string? Description, string[]? ExerciseIds, int? DurationMinutes);

public record CreateExerciseRequest(string Name, string? Description, string? Category, string? MediaUrl, string? MuscleGroup);
public record UpdateExerciseRequest(string? Name, string? Description, string? Category, string? MediaUrl, string? MuscleGroup);

public record CreateClubRequest(string Name, string? Description, string? Location);
public record UpdateClubRequest(string? Name, string? Description, string? Location);
