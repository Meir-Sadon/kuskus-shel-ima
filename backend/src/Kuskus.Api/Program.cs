using System.Threading.RateLimiting;
using Kuskus.Api.Auth;
using Kuskus.Api.Controllers;
using Kuskus.Api.Data;
using Kuskus.Api.Http;
using Kuskus.Api.Images;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

// `dotnet run -- hash-password [password]` prints a hash for Admin__PasswordHash.
if (args.Length > 0 && args[0] == "hash-password")
{
    var password = args.Length > 1 ? args[1] : Console.ReadLine();
    if (string.IsNullOrEmpty(password))
    {
        Console.Error.WriteLine("Usage: dotnet run -- hash-password <password>   (or pipe it on stdin)");
        return 1;
    }
    Console.WriteLine(AdminPasswordHasher.Hash(password));
    return 0;
}

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(ConnectionStringNormalizer.Normalize(config.GetConnectionString("Default"))));

builder.Services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
builder.Services.Configure<AdminOptions>(config.GetSection(AdminOptions.Section));
builder.Services.Configure<Kuskus.Api.Auth.CookieOptions>(config.GetSection(Kuskus.Api.Auth.CookieOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AdminTokenService>();

var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (System.Text.Encoding.UTF8.GetByteCount(jwt.Secret) < 32)
    throw new InvalidOperationException("Jwt:Secret must be set and at least 32 bytes long.");
var cookieName = config.GetSection(Kuskus.Api.Auth.CookieOptions.Section).Get<Kuskus.Api.Auth.CookieOptions>()?.Name
    ?? new Kuskus.Api.Auth.CookieOptions().Name;

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Issuer,
            IssuerSigningKey = AdminTokenService.SigningKey(jwt.Secret),
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        // The token lives in an httpOnly cookie rather than the Authorization header.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                context.Token = context.Request.Cookies[cookieName];
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

var loginLimit = config.GetSection(AdminOptions.Section).Get<AdminOptions>()?.LoginAttemptsPerMinute
    ?? new AdminOptions().LoginAttemptsPerMinute;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AdminAuthController.LoginRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = loginLimit,
                Window = TimeSpan.FromMinutes(1),
            }));
});

// Behind a reverse proxy (nginx in docker-compose, or the host's load balancer),
// take the client address from the one proxy in front of us.
if (config.GetValue<bool>("ForwardedHeaders:Enabled"))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

// Pictures go to Cloudinary when Cloudinary__Url is set; otherwise uploads answer 503.
var cloudinaryUrl = config["Cloudinary:Url"];
if (string.IsNullOrWhiteSpace(cloudinaryUrl))
{
    builder.Services.AddSingleton<IImageStore, NotConfiguredImageStore>();
}
else
{
    builder.Services.AddSingleton(new CloudinaryDotNet.Cloudinary(cloudinaryUrl) { Api = { Secure = true } });
    builder.Services.AddSingleton<IImageStore, CloudinaryImageStore>();
}

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

if (config.GetValue<bool>("ForwardedHeaders:Enabled"))
    app.UseForwardedHeaders();

app.UseMiddleware<RequireRequestHeaderMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

await DatabaseInitializer.InitializeAsync(app.Services, migrate: config.GetValue<bool>("Database:MigrateOnStartup"));

app.Run();
return 0;

public partial class Program;
