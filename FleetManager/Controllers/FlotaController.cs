using System.Text;
using FleetManager.Models;
using FleetManager.Services;
using Microsoft.AspNetCore.Mvc;

namespace FleetManager.Controllers;

public class FlotaController : Controller
{
    private readonly IFlotaRepositorio _repo;
    public FlotaController(IFlotaRepositorio repo) => _repo = repo;

    // GET /  -> tablero Fleet Manager
    [HttpGet("/")]
    public IActionResult Index() => View();

    // GET /api/datos -> equipos, catálogos, historial y snapshots (JSON)
    [HttpGet("/api/datos")]
    public async Task<IActionResult> Datos(CancellationToken ct) => Json(await _repo.ObtenerDatosAsync(ct));

    // POST /api/guardar -> botón Save de una fila
    [HttpPost("/api/guardar")]
    public async Task<IActionResult> Guardar([FromBody] GuardarFilaRequest req, CancellationToken ct)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.NumeroEquipo))
            return BadRequest(new ResultadoGuardar { Ok = false, Mensaje = "Equipment Number is required" });
        var r = await _repo.GuardarFilaAsync(req, UsuarioDe(req.Usuario), IpCliente(), ct);
        return r.Ok ? Json(r) : BadRequest(r);
    }

    // POST /api/snapshot -> botón Weekly Snapshot
    [HttpPost("/api/snapshot")]
    public async Task<IActionResult> Snapshot([FromBody] UsuarioRequest? req, CancellationToken ct)
        => Json(await _repo.CrearSnapshotAsync(UsuarioDe(req?.Usuario), IpCliente(), ct));

    // GET /api/export.csv -> botón Export CSV
    [HttpGet("/api/export.csv")]
    public async Task<IActionResult> ExportCsv(CancellationToken ct)
    {
        var d = await _repo.ObtenerDatosAsync(ct);
        var sb = new StringBuilder();
        sb.AppendLine(Csv("Equipment Number", "Group", "Truck / Pulling Trailer", "Location", "Status", "Load", "Updated By", "Updated At"));
        foreach (var e in d.Equipos)
            sb.AppendLine(Csv(e.NumeroEquipo, e.Grupo, e.EsCamion ? e.NumeroRemolque : e.NumeroCamion,
                e.Ubicacion, e.Estado, e.Carga, e.ActualizadoPor, e.FechaActualizacion.ToString("o")));
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", "Fleet_Manager_Export.csv");
    }

    private static string Csv(params string?[] v) =>
        string.Join(",", v.Select(x => "\"" + (x ?? "").Replace("\"", "\"\"") + "\""));

    private static string UsuarioDe(string? u)
    {
        u = (u ?? "").Trim();
        if (u.Length > 100) u = u[..100];
        return u.Length == 0 ? "Owner" : u;
    }

    private string? IpCliente()
    {
        // Railway y otros proxies envían X-Forwarded-For; ForwardedHeaders (Program.cs) ya lo aplica a RemoteIpAddress.
        var ip = HttpContext.Connection.RemoteIpAddress;
        if (ip == null) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }
}
