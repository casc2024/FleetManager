using FleetManager.Models;

namespace FleetManager.Services;

/// <summary>Acceso a datos del módulo de mezcladoras (esquema PostgreSQL "mezcladoras").</summary>
public interface IMezcladorasRepositorio
{
    Task VerificarConexionAsync(CancellationToken ct = default);

    Task<DatosMezcladoras> ObtenerDatosAsync(CancellationToken ct = default);
    Task<ResumenFlota> ObtenerResumenAsync(CancellationToken ct = default);

    // Camiones
    Task<Resultado> AgregarCamionAsync(CamionRequest req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> ActualizarCamionAsync(CamionRequest req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> EliminarCamionAsync(int numero, string usuario, string? ip, CancellationToken ct = default);

    // Conductores
    Task<Resultado> AgregarConductorAsync(ConductorRequest req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> ActualizarConductorAsync(ConductorRequest req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> EliminarConductorAsync(long idConductor, string usuario, string? ip, CancellationToken ct = default);

    // Asignación conductor ↔ camión
    Task<Resultado> AsignarConductorAsync(AsignacionRequest req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> QuitarConductorDeCamionAsync(long idConductor, string usuario, string? ip, CancellationToken ct = default);

    // Informe por correo
    Task<ConfigReporte> ObtenerConfigReporteAsync(CancellationToken ct = default);
    Task<Resultado> GuardarConfigReporteAsync(ConfigReporte config, string usuario, string? ip, CancellationToken ct = default);
    Task<List<EnvioReporte>> ObtenerEnviosAsync(int limite = 20, CancellationToken ct = default);
    Task RegistrarEnvioAsync(string disparo, string destinatarios, string asunto, bool exito, string? error,
                             ResumenFlota resumen, string usuario, string? ip, CancellationToken ct = default);

    /// <summary>
    /// Marca de forma atómica que esta instancia se queda con el envío de la ocurrencia indicada.
    /// Devuelve false si otra réplica ya lo tomó (evita correos duplicados con varias instancias).
    /// </summary>
    Task<bool> IntentarReclamarEnvioAsync(DateTime ocurrenciaUtc, CancellationToken ct = default);
}
