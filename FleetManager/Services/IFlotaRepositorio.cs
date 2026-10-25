using FleetManager.Models;

namespace FleetManager.Services;

/// <summary>Fleet board data access (PostgreSQL schema "fleet").</summary>
public interface IFlotaRepositorio
{
    /// <summary>Checks that the database responds and the fleet schema exists.</summary>
    Task VerificarConexionAsync(CancellationToken ct = default);

    Task<DatosTablero> ObtenerDatosAsync(CancellationToken ct = default);

    // Fleet Control Board
    Task<Resultado> GuardarFilaAsync(GuardarFilaRequest req, string usuario, string? ip, CancellationToken ct = default);

    // Equipment administration
    Task<Resultado> AgregarEquipoAsync(EquipoRequest req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> EliminarEquipoAsync(string numeroEquipo, string usuario, string? ip, CancellationToken ct = default);

    // Plants
    Task<Resultado> AgregarPlantaAsync(string codigo, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> EliminarPlantaAsync(string codigo, string usuario, string? ip, CancellationToken ct = default);

    // Drivers
    Task<Resultado> AgregarConductorAsync(ConductorFlotaRequest req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> ActualizarConductorAsync(ConductorFlotaRequest req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> EliminarConductorAsync(long idConductor, string usuario, string? ip, CancellationToken ct = default);

    // Weekly snapshot
    Task<SnapshotItem> CrearSnapshotAsync(string usuario, string? ip, CancellationToken ct = default);

    // Daily truck performance
    Task<List<PerfFila>> ListarPerfAsync(CancellationToken ct = default);
    Task<Resultado> GuardarPerfAsync(PerfRequest req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> EliminarPerfAsync(long idPerf, string usuario, string? ip, CancellationToken ct = default);
}
