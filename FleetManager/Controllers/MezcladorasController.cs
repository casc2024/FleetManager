using System.Text;
using FleetManager.Models;
using FleetManager.Services;
using Microsoft.AspNetCore.Mvc;

namespace FleetManager.Controllers;

/// <summary>
/// NBR Ready Mix — Control diario de mezcladoras.
/// Módulo independiente del tablero de flota (FlotaController): sus propias
/// vistas, modelos, repositorio y tablas (esquema PostgreSQL "mezcladoras").
/// </summary>
public class MezcladorasController : Controller
{
    private readonly IMezcladorasRepositorio _repo;
    private readonly IServicioReporteMezcladoras _reporte;
    private readonly IServicioCorreo _correo;

    public MezcladorasController(IMezcladorasRepositorio repo, IServicioReporteMezcladoras reporte, IServicioCorreo correo)
    {
        _repo = repo; _reporte = reporte; _correo = correo;
    }

    // ---------------------------------------------------------------- vista
    [HttpGet("/mixer")]
    public IActionResult Index() => View();

    // ---------------------------------------------------------------- datos
    [HttpGet("/api/mixer/datos")]
    public async Task<IActionResult> Datos(CancellationToken ct) => Json(await _repo.ObtenerDatosAsync(ct));

    // -------------------------------------------------------------- camiones
    [HttpPost("/api/mixer/camion/agregar")]
    public async Task<IActionResult> AgregarCamion([FromBody] CamionRequest req, CancellationToken ct)
        => Responder(await _repo.AgregarCamionAsync(req ?? new(), Usuario(req?.Usuario), Ip(), ct));

    [HttpPost("/api/mixer/camion/actualizar")]
    public async Task<IActionResult> ActualizarCamion([FromBody] CamionRequest req, CancellationToken ct)
        => Responder(await _repo.ActualizarCamionAsync(req ?? new(), Usuario(req?.Usuario), Ip(), ct));

    [HttpPost("/api/mixer/camion/eliminar")]
    public async Task<IActionResult> EliminarCamion([FromBody] CamionRequest req, CancellationToken ct)
        => Responder(await _repo.EliminarCamionAsync(req?.Numero ?? 0, Usuario(req?.Usuario), Ip(), ct));

    // ------------------------------------------------------------ conductores
    [HttpPost("/api/mixer/conductor/agregar")]
    public async Task<IActionResult> AgregarConductor([FromBody] ConductorRequest req, CancellationToken ct)
        => Responder(await _repo.AgregarConductorAsync(req ?? new(), Usuario(req?.Usuario), Ip(), ct));

    [HttpPost("/api/mixer/conductor/actualizar")]
    public async Task<IActionResult> ActualizarConductor([FromBody] ConductorRequest req, CancellationToken ct)
        => Responder(await _repo.ActualizarConductorAsync(req ?? new(), Usuario(req?.Usuario), Ip(), ct));

    [HttpPost("/api/mixer/conductor/eliminar")]
    public async Task<IActionResult> EliminarConductor([FromBody] ConductorRequest req, CancellationToken ct)
        => Responder(await _repo.EliminarConductorAsync(req?.IdConductor ?? 0, Usuario(req?.Usuario), Ip(), ct));

    [HttpPost("/api/mixer/conductor/asignar")]
    public async Task<IActionResult> AsignarConductor([FromBody] AsignacionRequest req, CancellationToken ct)
        => Responder(await _repo.AsignarConductorAsync(req ?? new(), Usuario(req?.Usuario), Ip(), ct));

    [HttpPost("/api/mixer/conductor/quitar")]
    public async Task<IActionResult> QuitarConductor([FromBody] AsignacionRequest req, CancellationToken ct)
        => Responder(await _repo.QuitarConductorDeCamionAsync(req?.IdConductor ?? 0, Usuario(req?.Usuario), Ip(), ct));

    // ---------------------------------------------------------------- informe
    [HttpGet("/api/mixer/reporte/config")]
    public async Task<IActionResult> ConfigReporte(CancellationToken ct)
    {
        var cfg = await _repo.ObtenerConfigReporteAsync(ct);
        cfg.CorreoConfigurado = _correo.Configurado;
        cfg.ProveedorCorreo = _correo.Proveedor;
        return Json(new { config = cfg, envios = await _repo.ObtenerEnviosAsync(10, ct) });
    }

    [HttpPost("/api/mixer/reporte/config")]
    public async Task<IActionResult> GuardarConfigReporte([FromBody] GuardarConfigRequest req, CancellationToken ct)
        => Responder(await _repo.GuardarConfigReporteAsync(req?.Config ?? new(), Usuario(req?.Usuario), Ip(), ct));

    /// <summary>Vista previa del correo tal cual lo recibirán los destinatarios.</summary>
    [HttpGet("/api/mixer/reporte/preview")]
    public async Task<IActionResult> PreviewReporte(CancellationToken ct)
    {
        var (_, html) = await _reporte.GenerarAsync(ct);
        return Content(html, "text/html; charset=utf-8", Encoding.UTF8);
    }

    [HttpPost("/api/mixer/reporte/enviar")]
    public async Task<IActionResult> EnviarReporte([FromBody] EnvioRequest? req, CancellationToken ct)
        => Responder(await _reporte.EnviarAsync("MANUAL", Usuario(req?.Usuario), Ip(), req?.CorreoPrueba, ct));

    /// <summary>
    /// Disparo del informe desde un cron externo (Railway Cron, GitHub Actions, cron-job.org).
    /// Alternativa a la tarea en background: protegido con el token REPORTE_TOKEN.
    /// </summary>
    [HttpPost("/api/mixer/reporte/cron")]
    public async Task<IActionResult> EnviarReporteCron([FromQuery] string? token, CancellationToken ct)
    {
        var esperado = Environment.GetEnvironmentVariable("REPORTE_TOKEN");
        if (string.IsNullOrWhiteSpace(esperado)) return NotFound();
        if (!string.Equals(token, esperado, StringComparison.Ordinal)) return Unauthorized();
        return Responder(await _reporte.EnviarAsync("PROGRAMADO", "Cron", Ip(), null, ct));
    }

    // ---------------------------------------------------------------- CSV
    [HttpGet("/api/mixer/export.csv")]
    public async Task<IActionResult> ExportCsv(CancellationToken ct)
    {
        var d = await _repo.ObtenerDatosAsync(ct);
        var sb = new StringBuilder();
        sb.AppendLine(Csv("Truck", "Plant", "Status", "Drivers"));
        foreach (var c in d.Camiones)
            sb.AppendLine(Csv("#" + c.Numero, c.Planta ?? "Unassigned", c.EstadoNombre,
                string.Join("; ", c.Conductores.Select(x => x.Nombre))));
        sb.AppendLine();
        sb.AppendLine(Csv("Driver", "Assigned Plant", "Truck"));
        foreach (var x in d.Conductores)
            sb.AppendLine(Csv(x.Nombre, x.Planta ?? "Unassigned", x.NumeroCamion.HasValue ? "#" + x.NumeroCamion : "No truck"));
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", "NBR_Fleet_Status.csv");
    }

    // ---------------------------------------------------------------- apoyo
    private IActionResult Responder(Resultado r) => r.Ok ? Json(r) : BadRequest(r);

    private static string Csv(params string?[] v) =>
        string.Join(",", v.Select(x => "\"" + (x ?? "").Replace("\"", "\"\"") + "\""));

    private static string Usuario(string? u)
    {
        u = (u ?? "").Trim();
        if (u.Length > 100) u = u[..100];
        return u.Length == 0 ? "Owner" : u;
    }

    private string? Ip()
    {
        var ip = HttpContext.Connection.RemoteIpAddress;
        if (ip == null) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }
}
