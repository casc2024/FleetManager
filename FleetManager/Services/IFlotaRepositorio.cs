using FleetManager.Models;

namespace FleetManager.Services;

public interface IFlotaRepositorio
{
    /// <summary>Comprueba que la base de datos responde y que existe la vista flota.v_tablero_flota.</summary>
    Task VerificarConexionAsync(CancellationToken ct = default);

    Task<DatosTablero> ObtenerDatosAsync(CancellationToken ct = default);

    Task<ResultadoGuardar> GuardarFilaAsync(GuardarFilaRequest req, string usuario, string? ip, CancellationToken ct = default);

    Task<SnapshotItem> CrearSnapshotAsync(string usuario, string? ip, CancellationToken ct = default);
}
