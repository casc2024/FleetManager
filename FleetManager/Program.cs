using FleetManager.Services;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Railway asigna el puerto en la variable PORT
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddControllersWithViews();

// Acceso a datos: siempre PostgreSQL (cadena en appsettings.json o variable DATABASE_URL)
builder.Services.AddSingleton<IFlotaRepositorio, FlotaRepositorioPostgres>();

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

app.Run();
