using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AIOPatcher.Server.Data;
using AIOPatcher.Server.Endpoints;
using AIOPatcher.Server.Options;
using AIOPatcher.Server.Security;
using AIOPatcher.Server.Services;
using AIOPatcher.Server.Services.Integrations;
using AIOPatcher.Server.Services.Notifications;
using AIOPatcher.Server.Services.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;

// Docker-HEALTHCHECK: sofort beenden, ohne den ganzen Server zu starten.
if (HealthCheckCommand.IsInvocation(args))
{
    return await HealthCheckCommand.RunAsync();
}

// "admin ..." ist ein CLI-Befehl (Notfallzugang) und gehört nicht in die Konfiguration.
var cliArgs = AdminCli.IsCliInvocation(args) ? args : null;
var builder = WebApplication.CreateBuilder(cliArgs is null ? args : []);

var options = builder.Configuration.GetSection(AIOPatcherOptions.SectionName).Get<AIOPatcherOptions>() ?? new AIOPatcherOptions();
options.DataDirectory = Path.GetFullPath(options.DataDirectory, builder.Environment.ContentRootPath);
Directory.CreateDirectory(options.DataDirectory);
builder.Services.AddSingleton(options);

// Hintergrunddienste schreiben ihre Texte in der Server-Sprache; Anfragen überschreiben das per Accept-Language.
L.Default = L.Normalize(options.Language);

using var bootstrapLoggerFactory = LoggerFactory.Create(l => l.AddConsole());
var bootstrapLogger = bootstrapLoggerFactory.CreateLogger("AIOPatcher.Startup");
var ca = CertificateAuthority.LoadOrCreate(options.CaDirectory, options.PublicHostname, bootstrapLogger);
builder.Services.AddSingleton(ca);
builder.Services.AddSingleton(UpdateSigningKey.LoadOrCreate(options.CaDirectory, bootstrapLogger));

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

    // Proxy-Port: HTTP für den eigenen Reverse Proxy (TLS macht der Proxy); Anfragen sind signiert, siehe PublicChannelGate.
    if (options.AgentProxyPort > 0)
    {
        kestrel.ListenAnyIP(options.AgentProxyPort);
    }
});

// --- Services -------------------------------------------------------------------------------------
builder.Services.AddDbContext<AppDbContext>(db => db.UseSqlite($"Data Source={options.DatabasePath}"));

builder.Services.AddDataProtection()
    .SetApplicationName("AIOPatcher")
    .PersistKeysToFileSystem(new DirectoryInfo(options.DataProtectionDirectory));

var authentication = builder.Services.AddAuthentication(ApiTokenAuthenticationHandler.SelectorScheme)
    // Browser: Cookie. Skripte: "Authorization: Bearer aiop_…".
    .AddPolicyScheme(ApiTokenAuthenticationHandler.SelectorScheme, null, o => o.ForwardDefaultSelector = ctx =>
        ApiTokenAuthenticationHandler.HasToken(ctx) ? ApiTokenAuthenticationHandler.SchemeName : CookieAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenAuthenticationHandler.SchemeName, null)
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
        // Jede Anfrage gegen die DB prüfen: deaktiviert, gelöscht, Rolle/Passwort geändert -> Sitzung sofort ungültig.
        cookie.Events.OnValidatePrincipal = async ctx =>
        {
            var principal = ctx.Principal;
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            UserAccount? user = null;
            if (Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ctx.HttpContext.RequestAborted);
            }

            if (principal is null || user is null || user.IsDisabled || !user.IsSetupComplete
                || user.SecurityStamp != principal.FindFirstValue(UserAccounts.StampClaim)
                || user.Role.ToString() != principal.FindFirstValue(ClaimTypes.Role))
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
// SSO (OpenID Connect): Konfiguration kommt zur Laufzeit aus der Datenbank (SsoService), nicht aus appsettings.
authentication
    .AddCookie(AIOPatcher.Server.Services.Sso.SsoService.ExternalCookieScheme, external =>
    {
        // Nur für den Weg vom Anbieter zurück zu /api/auth/sso/complete, danach gelöscht.
        external.Cookie.Name = "__Host-aiopatcher-sso";
        external.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        external.Cookie.SameSite = SameSiteMode.Lax;
        external.ExpireTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddOpenIdConnect(AIOPatcher.Server.Services.Sso.SsoService.Scheme, _ => { });
builder.Services.AddSingleton<AIOPatcher.Server.Services.Sso.SsoService>();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>, AIOPatcher.Server.Services.Sso.SsoOptionsSetup>();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(UserAccounts.AdminPolicy, p => p.RequireRole(nameof(UserRole.Admin)))
    .AddPolicy(UserAccounts.OperatorPolicy, p => p.RequireRole(nameof(UserRole.Admin), nameof(UserRole.Operator)));

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
// API-Beschreibung für die API-Seite im Dashboard (/api/openapi/v1.json, nur angemeldet).
builder.Services.AddOpenApi();
// Ungültige Requests (kaputtes JSON usw.) immer als 400 beantworten, auch in Development (dort sonst 500).
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<SetupTokenService>();
builder.Services.AddSingleton<FeatureFlags>();
builder.Services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddHostedService<JobJanitor>();
builder.Services.AddSingleton<AgentDownloads>();
builder.Services.AddSingleton<ServerClock>();
builder.Services.AddHostedService<AutoPatchScheduler>();
builder.Services.AddHostedService<RunbookEngine>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<NotificationSenders>();
builder.Services.AddHttpClient(NotificationSenders.HttpClientName, c => c.DefaultRequestHeaders.UserAgent.ParseAdd("AIOPatcher"));
builder.Services.AddHostedService<NotificationDispatcher>();
builder.Services.AddHostedService<HostMonitor>();
builder.Services.AddSingleton<IntegrationHttp>();
builder.Services.AddSingleton<AIOPatcher.Server.Services.Monitoring.MonitoringPackages>();
builder.Services.AddSingleton<VirtualizationProviderFactory>();
builder.Services.AddSingleton<OpnsenseClientFactory>();
builder.Services.AddSingleton<IRunbookPreparation, RunbookPreparation>();
builder.Services.AddSingleton<OpnsenseMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OpnsenseMonitor>());
builder.Services.AddHttpClient(CveService.HttpClientName, c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("AIOPatcher");
    c.Timeout = TimeSpan.FromSeconds(120);
});
builder.Services.AddSingleton<CveService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CveService>());
builder.Services.AddSingleton<CveMirror>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CveMirror>());
builder.Services.AddSingleton<CveLookupService>();
builder.Services.AddHostedService<ContainerUpdateService>();
builder.Services.AddSingleton<PublicAccess>();
builder.Services.AddSingleton<ScanSchedule>();
builder.Services.AddSingleton<UpdateChangelogs>();
builder.Services.AddHttpClient("ai", c => c.Timeout = TimeSpan.FromMinutes(5)); // lokale Modelle brauchen etwas
builder.Services.AddSingleton<AIOPatcher.Server.Services.Ai.AiClient>();
builder.Services.AddSingleton<AIOPatcher.Server.Services.Ai.UpdateSummaries>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AIOPatcher.Server.Services.Ai.UpdateSummaries>());
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ReleaseCheck>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReleaseCheck>());

var app = builder.Build();

// --- Datenbank & Setup ----------------------------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DatabaseInitializer.InitializeAsync(db, options, app.Logger, CancellationToken.None);
    if (cliArgs is not null)
    {
        return await AdminCli.RunAsync(cliArgs, db, options, CancellationToken.None);
    }

    await app.Services.GetRequiredService<SetupTokenService>().InitializeAsync(db, app.Logger, CancellationToken.None);
    await app.Services.GetRequiredService<FeatureFlags>().LoadAsync(db, CancellationToken.None);
    await app.Services.GetRequiredService<AIOPatcher.Server.Services.Sso.SsoService>().LoadAsync(db, CancellationToken.None);
    await app.Services.GetRequiredService<PublicAccess>().LoadAsync(db, CancellationToken.None);
    await app.Services.GetRequiredService<ScanSchedule>().LoadAsync(db, CancellationToken.None);
    await app.Services.GetRequiredService<AIOPatcher.Server.Services.Ai.UpdateSummaries>().LoadAsync(db, CancellationToken.None);
}

// --- Pipeline -------------------------------------------------------------------------------------
app.UseExceptionHandler();
app.UseTrustedProxies(options);
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

app.UseRequestLanguage();
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
app.MapRunbookEndpoints();
app.MapUserEndpoints();
app.MapNotificationEndpoints();
app.MapPatchRuleEndpoints();
app.MapIntegrationEndpoints();
app.MapMetricsEndpoints();
app.MapApiTokenEndpoints();
app.MapReportEndpoints();
app.MapIpamEndpoints();
app.MapCheckEndpoints();
app.MapComplianceEndpoints();
app.MapMonitoringEndpoints();
app.MapComplianceBaselineEndpoints();
app.MapSsoEndpoints();
app.MapSoftwareEndpoints();
app.MapVulnerabilityEndpoints();
app.MapCveLookupEndpoints();
app.MapFeatureEndpoints();
app.MapPublicAccessEndpoints();
app.MapScanSettingsEndpoints();
app.MapChangelogEndpoints();
app.MapAiEndpoints();
app.MapPasskeyEndpoints();
app.MapOpenApi("/api/openapi/{documentName}.json").RequireAuthorization();
app.MapAgentEndpoints();
app.MapAgentDownloadEndpoints();

app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("Dashboard: https://{Host}:{DashboardPort}  |  Agent-API: https://{Host}:{AgentPort}  |  Proxy-Port (HTTP): {ProxyPort}",
    options.PublicHostname, options.DashboardPort, options.PublicHostname, options.AgentPort, options.AgentProxyPort > 0 ? options.AgentProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture) : "off");

await app.RunAsync();
return 0;
