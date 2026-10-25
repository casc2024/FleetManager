using FleetManager.Models;
using FleetManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FleetManager.Controllers;

/// <summary>User maintainer (admin only): list, create, edit and remove users.</summary>
[Authorize(Roles = IdentidadApp.RolAdmin)]
public class UsuariosController : Controller
{
    private readonly ISeguridadRepositorio _repo;
    public UsuariosController(ISeguridadRepositorio repo) => _repo = repo;

    // GET /users -> maintainer page
    [HttpGet("/users")]
    public IActionResult Index() => View();

    // GET /api/users -> list (never includes password hashes)
    [HttpGet("/api/users")]
    public async Task<IActionResult> Listar(CancellationToken ct) => Json(await _repo.ListarUsuariosAsync(ct));

    // POST /api/users -> create
    [HttpPost("/api/users")]
    public async Task<IActionResult> Crear([FromBody] AppUserRequest req, CancellationToken ct)
        => Responder(await _repo.CrearUsuarioAsync(req ?? new(), Usuario(), HttpContext.Ip(), ct));

    // POST /api/users/update -> edit
    [HttpPost("/api/users/update")]
    public async Task<IActionResult> Actualizar([FromBody] AppUserRequest req, CancellationToken ct)
        => Responder(await _repo.ActualizarUsuarioAsync(req ?? new(), Usuario(), HttpContext.Ip(), ct));

    // POST /api/users/delete -> soft delete
    [HttpPost("/api/users/delete")]
    public async Task<IActionResult> Eliminar([FromBody] AppUserRequest req, CancellationToken ct)
        => Responder(await _repo.EliminarUsuarioAsync(req?.IdUser ?? 0, User.Usuario()?.IdUser ?? 0, Usuario(), HttpContext.Ip(), ct));

    private IActionResult Responder(Resultado r) => r.Ok ? Json(r) : BadRequest(r);

    private string Usuario() => User.Usuario()?.Username ?? "unknown";
}
