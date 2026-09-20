using FleetManager.Services;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Configuración local NO versionada (cadena de conexión para desarrollo): appsettings.Local.json
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Railway asigna el puerto en la variable PORT
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddControllersWithViews();

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
app.MapControllers();
app.MapGet("/error", () => Results.Problem("Unexpected error"));
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

// Comprobar la conexión a la base de datos al arrancar
await app.Services.GetRequiredService<IFlotaRepositorio>().VerificarConexionAsync();

// El módulo de mezcladoras es opcional: si su esquema aún no existe, la app igual arranca
try { await app.Services.GetRequiredService<IMezcladorasRepositorio>().VerificarConexionAsync(); }
catch (Exception ex)
{
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Mezcladoras")
       .LogWarning("Módulo de mezcladoras no disponible ({msg}). Ejecute db/mezcladoras_esquema.sql en la base.", ex.Message);
}

app.Run();
