using FleetManager.Models;

namespace FleetManager.Services;

/// <summary>Login and user maintenance (PostgreSQL schema "seguridad").</summary>
public interface ISeguridadRepositorio
{
    Task VerificarConexionAsync(CancellationToken ct = default);

    /// <summary>Returns the user when the username exists, is active and the password matches; otherwise null.</summary>
    Task<AppUser?> AutenticarAsync(string username, string contrasena, CancellationToken ct = default);

    Task<List<AppUser>> ListarUsuariosAsync(CancellationToken ct = default);

    Task<Resultado> CrearUsuarioAsync(AppUserRequest req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> ActualizarUsuarioAsync(AppUserRequest req, string usuario, string? ip, CancellationToken ct = default);
    /// <summary>Soft delete (is_active = false). <paramref name="idActual"/> is the signed-in user, who cannot delete themselves.</summary>
    Task<Resultado> EliminarUsuarioAsync(long idUser, long idActual, string usuario, string? ip, CancellationToken ct = default);
}
