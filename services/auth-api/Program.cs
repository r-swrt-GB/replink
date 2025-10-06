using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
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
                // Create unique constraint on AuthUser.id
                await tx.RunAsync("CREATE CONSTRAINT auth_user_id_unique IF NOT EXISTS FOR (u:AuthUser) REQUIRE u.id IS UNIQUE");

                // Create unique constraint on AuthUser.email
                await tx.RunAsync("CREATE CONSTRAINT auth_user_email_unique IF NOT EXISTS FOR (u:AuthUser) REQUIRE u.email IS UNIQUE");

                // Create unique constraint on AuthUser.username
                await tx.RunAsync("CREATE CONSTRAINT auth_user_username_unique IF NOT EXISTS FOR (u:AuthUser) REQUIRE u.username IS UNIQUE");

                // Create index on AuthUser.email for faster lookups
                await tx.RunAsync("CREATE INDEX auth_user_email_index IF NOT EXISTS FOR (u:AuthUser) ON (u.email)");

                // Create index on AuthUser.username for faster lookups
                await tx.RunAsync("CREATE INDEX auth_user_username_index IF NOT EXISTS FOR (u:AuthUser) ON (u.username)");
            });
            Log.Information("Neo4j constraints and indexes created for AuthUser");
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
    app.MapGet("/api/auth/health", () => Results.Ok(new { status = "Healthy", service = "Auth API (Neo4j)" }));

    // Register endpoint
    app.MapPost("/api/auth/register", async (RegisterRequest request, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();

        // Check if email or username already exists
        var existingUser = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (u:AuthUser)
                WHERE u.email = $email OR u.username = $username
                RETURN u.email AS email, u.username AS username
            ";

            var cursor = await tx.RunAsync(query, new { email = request.Email, username = request.Username });
            var records = await cursor.ToListAsync();

            if (records.Count == 0)
                return null;

            var record = records[0];
            return new
            {
                Email = record["email"].As<string>(),
                Username = record["username"].As<string>()
            };
        });

        if (existingUser != null)
        {
            if (existingUser.Email == request.Email)
            {
                return Results.BadRequest(new { error = "Email already registered" });
            }
            if (existingUser.Username == request.Username)
            {
                return Results.BadRequest(new { error = "Username already taken" });
            }
        }

        // Create new user
        var userId = Guid.NewGuid().ToString();
        var createdAt = DateTime.UtcNow;
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);

        var user = await session.ExecuteWriteAsync(async tx =>
        {
            var query = @"
                CREATE (u:AuthUser {
                    id: $id,
                    email: $email,
                    username: $username,
                    passwordHash: $passwordHash,
                    role: $role,
                    createdAt: datetime($createdAt)
                })
                RETURN u.id AS id,
                       u.email AS email,
                       u.username AS username,
                       u.role AS role,
                       u.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, new
            {
                id = userId,
                email = request.Email,
                username = request.Username,
                passwordHash = passwordHash,
                role = request.Role ?? "athlete",
                createdAt = createdAt.ToString("o")
            });

            var record = await cursor.SingleAsync();

            return new User
            {
                Id = record["id"].As<string>(),
                Email = record["email"].As<string>(),
                Username = record["username"].As<string>(),
                Role = record["role"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            };
        });

        Log.Information("User registered: {Email}", request.Email);

        return Results.Ok(new { message = "User registered successfully", userId = user.Id });
    });

    // Login endpoint
    app.MapPost("/api/auth/login", async (LoginRequest request, IDriver driver) =>
    {
        await using var session = driver.AsyncSession();

        var user = await session.ExecuteReadAsync(async tx =>
        {
            var query = @"
                MATCH (u:AuthUser {email: $email})
                RETURN u.id AS id,
                       u.email AS email,
                       u.username AS username,
                       u.passwordHash AS passwordHash,
                       u.role AS role,
                       u.createdAt AS createdAt
            ";

            var cursor = await tx.RunAsync(query, new { email = request.Email });
            var records = await cursor.ToListAsync();

            if (records.Count == 0)
                return null;

            var record = records[0];
            return new User
            {
                Id = record["id"].As<string>(),
                Email = record["email"].As<string>(),
                Username = record["username"].As<string>(),
                PasswordHash = record["passwordHash"].As<string>(),
                Role = record["role"].As<string>(),
                CreatedAt = record["createdAt"].As<DateTime>()
            };
        });

        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return Results.Unauthorized();
        }

        var token = GenerateJwtToken(user, jwtSecret, jwtIssuer, jwtAudience);

        Log.Information("User logged in: {Email}", request.Email);

        return Results.Ok(new
        {
            token,
            userId = user.Id,
            email = user.Email,
            username = user.Username,
            role = user.Role
        });
    });

    Log.Information("Auth API (Neo4j) starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Auth API failed to start");
}
finally
{
    Log.CloseAndFlush();
}

static string GenerateJwtToken(User user, string secret, string issuer, string audience)
{
    var tokenHandler = new JwtSecurityTokenHandler();
    var key = Encoding.UTF8.GetBytes(secret);
    var tokenDescriptor = new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role)
        }),
        Expires = DateTime.UtcNow.AddDays(7),
        Issuer = issuer,
        Audience = audience,
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
    };
    var token = tokenHandler.CreateToken(tokenDescriptor);
    return tokenHandler.WriteToken(token);
}

// Models
public class User
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "athlete"; // athlete, coach, influencer
    public DateTime CreatedAt { get; set; }
}

public record RegisterRequest(string Email, string Username, string Password, string? Role);
public record LoginRequest(string Email, string Password);
