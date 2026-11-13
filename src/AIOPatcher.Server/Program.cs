using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(AIOPatcherOptions.SectionName).Get<AIOPatcherOptions>() ?? new AIOPatcherOptions();
options.DataDirectory = Path.GetFullPath(options.DataDirectory, builder.Environment.ContentRootPath);
Directory.CreateDirectory(options.DataDirectory);
builder.Services.AddSingleton(options);

using var bootstrapLoggerFactory = LoggerFactory.Create(l => l.AddConsole());
var bootstrapLogger = bootstrapLoggerFactory.CreateLogger("AIOPatcher.Startup");
var ca = CertificateAuthority.LoadOrCreate(options.CaDirectory, options.PublicHostname, bootstrapLogger);
builder.Services.AddSingleton(ca);

// --- Kestrel: zwei getrennte Listener -------------------------------------------------------------
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;

    // Dashboard: kein Client-Zertifikat.
    kestrel.ListenAnyIP(options.DashboardPort, listen => listen.UseHttps(https =>
    {
        https.ServerCertificateSelector = (_, _) => ca.GetServerCertificate();
    }));

    // Agent-API: Client-Zertifikat erlaubt (Pflicht für alles außer /agent/enroll, siehe PortIsolation).
    kestrel.ListenAnyIP(options.AgentPort, listen => listen.UseHttps(https =>
    {
        https.ServerCertificateSelector = (_, _) => ca.GetServerCertificate();
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        https.ClientCertificateValidation = (certificate, _, _) => ca.IsIssuedByThisCa(certificate);
    }));
});

// --- Services -------------------------------------------------------------------------------------
builder.Services.AddDbContext<AppDbContext>(db => db.UseSqlite($"Data Source={options.DatabasePath}"));

builder.Services.AddDataProtection()
    .SetApplicationName("AIOPatcher")
    .PersistKeysToFileSystem(new DirectoryInfo(options.DataProtectionDirectory));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.Cookie.Name = "__Host-aiopatcher";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        cookie.Cookie.SameSite = SameSiteMode.Strict;
        cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
        cookie.SlidingExpiration = true;
        // API-Aufrufe bekommen 401/403 statt Redirect.
        cookie.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        cookie.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(AuthEndpoints.RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddProblemDetails();
// Ungültige Requests (kaputtes JSON usw.) immer als 400 beantworten, auch in Development (dort sonst 500).
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<SetupTokenService>();
builder.Services.AddSingleton<IPasswordHasher<AdminUser>, PasswordHasher<AdminUser>>();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddHostedService<JobJanitor>();
builder.Services.AddSingleton<AgentDownloads>();

var app = builder.Build();

// --- Datenbank & Setup ----------------------------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DatabaseInitializer.InitializeAsync(db, options, app.Logger, CancellationToken.None);
    await app.Services.GetRequiredService<SetupTokenService>().InitializeAsync(db, app.Logger, CancellationToken.None);
}

// --- Pipeline -------------------------------------------------------------------------------------
app.UseExceptionHandler();
app.UsePortIsolation(options);

app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
    headers.StrictTransportSecurity = "max-age=31536000";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();
app.MapSiteEndpoints();
app.MapGet("/api/ca", (CertificateAuthority authority) => Results.Ok(new { fingerprintSha256 = authority.RootFingerprintSha256, pem = authority.RootCertificatePem }))
    .RequireAuthorization();
app.MapHostEndpoints();
app.MapJobEndpoints();
app.MapAgentEndpoints();
app.MapAgentDownloadEndpoints();

app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("Dashboard: https://{Host}:{DashboardPort}  |  Agent-API: https://{Host}:{AgentPort}",
    options.PublicHostname, options.DashboardPort, options.PublicHostname, options.AgentPort);

await app.RunAsync();
