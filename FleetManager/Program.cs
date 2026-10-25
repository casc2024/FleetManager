using FleetManager.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Configuración local NO versionada (cadena de conexión para desarrollo): appsettings.Local.json
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Railway asigna el puerto en la variable PORT
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddControllersWithViews();

// --- Seguridad: login por cookie + mantenedor de usuarios (esquema "seguridad") ---
builder.Services.AddSingleton<ISeguridadRepositorio, SeguridadRepositorioPostgres>();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/login";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
        o.Cookie.Name = "FleetAuth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        // Las llamadas a /api deben recibir 401/403 (JSON), no una redirección al HTML de login.
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = 401; return Task.CompletedTask; }
            ctx.Response.Redirect(ctx.RedirectUri); return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = 403; return Task.CompletedTask; }
            ctx.Response.Redirect(ctx.RedirectUri); return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

// Acceso a datos: siempre PostgreSQL (cadena en appsettings.json o variable DATABASE_URL)
builder.Services.AddSingleton<IFlotaRepositorio, FlotaRepositorioPostgres>();

// --- NBR Ready Mix: control diario de mezcladoras (esquema "mezcladoras") ---
builder.Services.AddSingleton<IMezcladorasRepositorio, MezcladorasRepositorioPostgres>();
builder.Services.AddHttpClient();                                   // para la API de correo (Resend)
builder.Services.AddSingleton<IServicioCorreo, ServicioCorreo>();
builder.Services.AddSingleton<IServicioReporteMezcladoras, ServicioReporteMezcladoras>();
builder.Services.AddHostedService<TareaReporteProgramado>();        // envío programado del informe

// IP real del cliente detrás del proxy de Railway (X-Forwarded-For)
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/error");
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/error", () => Results.Problem("Unexpected error"));
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

// Tablero de flota (esquema "fleet"): si aún no existe, la app arranca igual
try { await app.Services.GetRequiredService<IFlotaRepositorio>().VerificarConexionAsync(); }
catch (Exception ex)
{
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Fleet")
       .LogWarning("Tablero de flota no disponible ({msg}). Ejecute db/fleet_v15_esquema.sql en la base.", ex.Message);
}

// El módulo de mezcladoras es opcional: si su esquema aún no existe, la app igual arranca
try { await app.Services.GetRequiredService<IMezcladorasRepositorio>().VerificarConexionAsync(); }
catch (Exception ex)
{
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Mezcladoras")
       .LogWarning("Módulo de mezcladoras no disponible ({msg}). Ejecute db/mezcladoras_esquema.sql en la base.", ex.Message);
}

// Seguridad: el esquema debe existir para poder iniciar sesión. Si no está, la app arranca
// igual (solo registra la advertencia) para poder ejecutar db/seguridad_esquema.sql.
try { await app.Services.GetRequiredService<ISeguridadRepositorio>().VerificarConexionAsync(); }
catch (Exception ex)
{
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Seguridad")
       .LogWarning("Módulo de seguridad no disponible ({msg}). Ejecute db/seguridad_esquema.sql en la base.", ex.Message);
}

app.Run();
