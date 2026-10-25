using FleetManager.Models;
using FleetManager.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FleetManager.Controllers;

/// <summary>Sign-in, sign-out and "who am I" for the top menu.</summary>
public class CuentaController : Controller
{
    private readonly ISeguridadRepositorio _repo;
    public CuentaController(ISeguridadRepositorio repo) => _repo = repo;

    // GET /login -> sign-in page (already signed in -> go home)
    [HttpGet("/login")]
    [AllowAnonymous]
    public IActionResult Login()
        => User.Identity?.IsAuthenticated == true ? Redirect("/") : View();

    // POST /api/auth/login
    [HttpPost("/api/auth/login")]
    [AllowAnonymous]
    public async Task<IActionResult> Autenticar([FromBody] LoginRequest req, CancellationToken ct)
    {
        var user = await _repo.AutenticarAsync(req?.Username ?? "", req?.Password ?? "", ct);
        if (user == null)
            return Unauthorized(Resultado.Mal("Invalid email or password."));

        var identity = new System.Security.Claims.ClaimsIdentity(
            IdentidadApp.Claims(user), CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new System.Security.Claims.ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7) });

        return Json(Resultado.Bien("Signed in.", new { isAdmin = user.IsAdmin }));
    }

    // POST /api/auth/logout
    [HttpPost("/api/auth/logout")]
    public async Task<IActionResult> Salir()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Json(Resultado.Bien("Signed out."));
    }

    // GET /api/auth/me -> current user for the menu (name + admin flag)
    [HttpGet("/api/auth/me")]
    public IActionResult Yo()
    {
        var u = User.Usuario();
        return u == null ? Unauthorized() : Json(new { u.Username, u.Name, u.Email, u.IsAdmin });
    }
}
