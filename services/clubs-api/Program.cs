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
                // Create unique constraint on Club.id
                await tx.RunAsync("CREATE CONSTRAINT club_id_unique IF NOT EXISTS FOR (c:Club) REQUIRE c.id IS UNIQUE");

                // Create index on Club.name for faster searches
                await tx.RunAsync("CREATE INDEX club_name_index IF NOT EXISTS FOR (c:Club) ON (c.name)");

                // Create index on Club.location for filtering
                await tx.RunAsync("CREATE INDEX club_location_index IF NOT EXISTS FOR (c:Club) ON (c.location)");
            });
            Log.Information("Neo4j constraints and indexes created for Clubs");
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
    app.MapGet("/api/clubs/health", () => Results.Ok(new { status = "Healthy", service = "Clubs API (Neo4j)" }));

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
                CreatedAt = record["createdAt"].As<DateTime>()
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
                CreatedAt = record["createdAt"].As<DateTime>()
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
                CreatedAt = record["createdAt"].As<DateTime>()
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
            // Build dynamic SET clause based on provided fields
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
                // No updates, just return existing club
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
                    CreatedAt = getRecord["createdAt"].As<DateTime>()
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
                CreatedAt = record["createdAt"].As<DateTime>()
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
            // First check if club exists
            var checkQuery = "MATCH (c:Club {id: $id}) RETURN c";
            var checkCursor = await tx.RunAsync(checkQuery, new { id });
            var exists = await checkCursor.FetchAsync();

            if (!exists)
                return false;

            // Delete the club and all its relationships
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

    // BONUS: Get clubs with member count (leveraging Neo4j relationships)
    app.MapGet("/api/clubs/with-stats", async (IDriver driver, int? limit, int? offset) =>
    {
        await using var session = driver.AsyncSession();
        var clubs = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (c:Club)
                OPTIONAL MATCH (c)<-[:TRAINS_AT]-(u:User)
                RETURN c.id AS id,
                       c.name AS name,
                       c.description AS description,
                       c.location AS location,
                       c.createdAt AS createdAt,
                       count(DISTINCT u) AS memberCount
                ORDER BY c.name
                SKIP $offset
                LIMIT $limit
            ";

            var cursor = await tx.RunAsync(query, new {
                offset = offset ?? 0,
                limit = limit ?? 50
            });

            var records = await cursor.ToListAsync();
            return records.Select(record => new
            {
                Id = record["id"].As<string>(),
                Name = record["name"].As<string>(),
                Description = record["description"].As<string>(),
                Location = record["location"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTime>(),
                MemberCount = record["memberCount"].As<int>()
            }).ToList();
        });

        return Results.Ok(clubs);
    }).RequireAuthorization();

    // BONUS: Search clubs by name or location
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
                CreatedAt = record["createdAt"].As<DateTime>()
            }).ToList();
        });

        return Results.Ok(clubs);
    }).RequireAuthorization();

    Log.Information("Clubs API (Neo4j) starting on port 80");

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

// Models
public class Club
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public record CreateClubRequest(string Name, string? Description, string? Location);
public record UpdateClubRequest(string? Name, string? Description, string? Location);
