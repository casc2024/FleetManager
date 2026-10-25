using System.Text;
using FleetManager.Models;
using FleetManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FleetManager.Controllers;

[Authorize]
public class FlotaController : Controller
{
    private readonly IFlotaRepositorio _repo;
    public FlotaController(IFlotaRepositorio repo) => _repo = repo;

    // GET /  -> Fleet board
    [HttpGet("/")]
    public IActionResult Index() => View();

    // GET /api/datos -> equipment, plants, drivers, history and snapshots (JSON)
    [HttpGet("/api/datos")]
    public async Task<IActionResult> Datos(CancellationToken ct) => Json(await _repo.ObtenerDatosAsync(ct));

    // POST /api/guardar -> Save button on a board row
    [HttpPost("/api/guardar")]
    public async Task<IActionResult> Guardar([FromBody] GuardarFilaRequest req, CancellationToken ct)
        => Responder(await _repo.GuardarFilaAsync(req ?? new(), Usuario(), Ip(), ct));

    // Equipment administration
    [HttpPost("/api/equipo/agregar")]
    public async Task<IActionResult> AgregarEquipo([FromBody] EquipoRequest req, CancellationToken ct)
        => Responder(await _repo.AgregarEquipoAsync(req ?? new(), Usuario(), Ip(), ct));

    [HttpPost("/api/equipo/eliminar")]
    public async Task<IActionResult> EliminarEquipo([FromBody] EquipoRequest req, CancellationToken ct)
        => Responder(await _repo.EliminarEquipoAsync(req?.NumeroEquipo ?? "", Usuario(), Ip(), ct));

    // Plants
    [HttpPost("/api/planta/agregar")]
    public async Task<IActionResult> AgregarPlanta([FromBody] PlantaRequest req, CancellationToken ct)
        => Responder(await _repo.AgregarPlantaAsync(req?.Codigo ?? "", Usuario(), Ip(), ct));

    [HttpPost("/api/planta/eliminar")]
    public async Task<IActionResult> EliminarPlanta([FromBody] PlantaRequest req, CancellationToken ct)
        => Responder(await _repo.EliminarPlantaAsync(req?.Codigo ?? "", Usuario(), Ip(), ct));

    // Drivers
    [HttpPost("/api/conductor/agregar")]
    public async Task<IActionResult> AgregarConductor([FromBody] ConductorFlotaRequest req, CancellationToken ct)
        => Responder(await _repo.AgregarConductorAsync(req ?? new(), Usuario(), Ip(), ct));

    [HttpPost("/api/conductor/actualizar")]
    public async Task<IActionResult> ActualizarConductor([FromBody] ConductorFlotaRequest req, CancellationToken ct)
        => Responder(await _repo.ActualizarConductorAsync(req ?? new(), Usuario(), Ip(), ct));

    [HttpPost("/api/conductor/eliminar")]
    public async Task<IActionResult> EliminarConductor([FromBody] ConductorFlotaRequest req, CancellationToken ct)
        => Responder(await _repo.EliminarConductorAsync(req?.IdConductor ?? 0, Usuario(), Ip(), ct));

    // Weekly snapshot
    [HttpPost("/api/snapshot")]
    public async Task<IActionResult> Snapshot(CancellationToken ct)
        => Json(await _repo.CrearSnapshotAsync(Usuario(), Ip(), ct));

    // Daily truck performance
    [HttpGet("/api/perf")]
    public async Task<IActionResult> Perf(CancellationToken ct) => Json(await _repo.ListarPerfAsync(ct));

    [HttpPost("/api/perf/guardar")]
    public async Task<IActionResult> GuardarPerf([FromBody] PerfRequest req, CancellationToken ct)
        => Responder(await _repo.GuardarPerfAsync(req ?? new(), Usuario(), Ip(), ct));

    [HttpPost("/api/perf/eliminar")]
    public async Task<IActionResult> EliminarPerf([FromBody] PerfRequest req, CancellationToken ct)
        => Responder(await _repo.EliminarPerfAsync(req?.IdPerf ?? 0, Usuario(), Ip(), ct));

    // GET /api/export.csv -> Export the board
    [HttpGet("/api/export.csv")]
    public async Task<IActionResult> ExportCsv(CancellationToken ct)
    {
        var d = await _repo.ObtenerDatosAsync(ct);
        // Resolve, for each truck, the trailer it pulls (trailer.truck_number = truck).
        var remolquePorCamion = d.Equipos
            .Where(e => e.Grupo.StartsWith("TRAILER") && !string.IsNullOrEmpty(e.NumeroCamion))
            .GroupBy(e => e.NumeroCamion!)
            .ToDictionary(g => g.Key, g => g.First().NumeroEquipo);

        var sb = new StringBuilder();
        sb.AppendLine(Csv("Equipment Number", "Group", "Plant (Trucks)", "Truck / Pulling Trailer", "Location", "Status", "Load", "Updated By", "Updated At"));
        foreach (var e in d.Equipos)
        {
            var esCamion = e.Grupo.StartsWith("TRUCK");
            var pairing = esCamion
                ? (remolquePorCamion.TryGetValue(e.NumeroEquipo, out var t) ? t : "")
                : e.NumeroCamion;
            sb.AppendLine(Csv(e.NumeroEquipo, e.Grupo, esCamion ? e.PlantaCamion : "",
                pairing, e.Ubicacion, e.Estado, e.Carga, e.ActualizadoPor, e.FechaActualizacion.ToString("o")));
        }
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", "Fleet_Manager_Export.csv");
    }

    // ---------------------------------------------------------------- helpers
    private IActionResult Responder(Resultado r) => r.Ok ? Json(r) : BadRequest(r);

    private static string Csv(params string?[] v) =>
        string.Join(",", v.Select(x => "\"" + (x ?? "").Replace("\"", "\"\"") + "\""));

    private string Usuario() => User.Usuario()?.Username ?? "Owner";

    private string? Ip() => HttpContext.Ip();
}
