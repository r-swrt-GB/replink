using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;
using Serilog;
using System.Text;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Add Serilog
    builder.Host.UseSerilog();

    // Load Ocelot configuration
    builder.Configuration.AddJsonFile("ocelot.json", optional: false, reloadOnChange: true);

    // Configure JWT Authentication
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

    // Add Ocelot
    builder.Services.AddOcelot();

    // Add HttpClient for health check aggregation
    builder.Services.AddHttpClient();

    // Add CORS
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowAll", policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    app.UseCors("AllowAll");

    // Gateway health check endpoint
    app.MapGet("/health", () => Results.Ok(new
    {
        status = "Healthy",
        service = "API Gateway",
        timestamp = DateTime.UtcNow
    }));

    // Aggregated health check endpoint
    app.MapGet("/health/all", async (IHttpClientFactory httpClientFactory) =>
    {
        var services = new Dictionary<string, string>
        {
            { "auth-api", "http://auth-api:80/api/auth/health" },
            { "users-api", "http://users-api:80/api/users/health" },
            { "content-api", "http://content-api:80/api/content/health" },
            { "fitness-api", "http://fitness-api:80/api/fitness/health" },
            { "socialgraph-api", "http://socialgraph-api:80/api/graph/health" },
            { "feed-api", "http://feed-api:80/api/feed/health" },
            { "analytics-api", "http://analytics-api:80/api/analytics/health" }
        };

        var healthStatuses = new Dictionary<string, object>();
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        foreach (var service in services)
        {
            try
            {
                var response = await client.GetAsync(service.Value);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    healthStatuses[service.Key] = new
                    {
                        status = "Healthy",
                        details = content
                    };
                }
                else
                {
                    healthStatuses[service.Key] = new
                    {
                        status = "Unhealthy",
                        statusCode = (int)response.StatusCode
                    };
                }
            }
            catch (Exception ex)
            {
                healthStatuses[service.Key] = new
                {
                    status = "Unavailable",
                    error = ex.Message
                };
            }
        }

        var allHealthy = healthStatuses.Values.All(v =>
        {
            var statusProp = v.GetType().GetProperty("status");
            return statusProp?.GetValue(v)?.ToString() == "Healthy";
        });

        return Results.Ok(new
        {
            gateway = "Healthy",
            overallStatus = allHealthy ? "Healthy" : "Degraded",
            services = healthStatuses,
            timestamp = DateTime.UtcNow
        });
    });

    // Use Ocelot middleware
    await app.UseOcelot();

    Log.Information("Ocelot API Gateway starting on port 80");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "API Gateway failed to start");
}
finally
{
    Log.CloseAndFlush();
}
