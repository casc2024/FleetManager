using FleetManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FleetManager.Controllers;

/// <summary>
/// JCK Sand Pit quoter (admin only). The quoter is a self-contained client-side
/// app (Quoter/index.html) served behind the auth cookie so only administrators
/// reach it. It keeps its own state in the browser (localStorage).
/// </summary>
[Authorize(Roles = IdentidadApp.RolAdmin)]
public class CotizadorController : Controller
{
    private readonly IWebHostEnvironment _env;
    public CotizadorController(IWebHostEnvironment env) => _env = env;

    // GET /quotes -> the quoter page
    [HttpGet("/quotes")]
    public IActionResult Index()
    {
        var path = Path.Combine(_env.ContentRootPath, "Quoter", "index.html");
        if (!System.IO.File.Exists(path)) return NotFound("Quoter not found.");
        return PhysicalFile(path, "text/html; charset=utf-8");
    }

    // GET /data.js -> the quoter catalog (products, carriers, payment terms, settings).
    // GET /app.js  -> the quoter application script (kept external so the dev-time
    //                 browser-refresh HTML injection cannot split an inline <script>).
    // The quoter page requests "./data.js" / "./app.js", which resolve to /data.js and /app.js.
    [HttpGet("/data.js")]
    public IActionResult DataJs() => QuoterScript("data.js");

    [HttpGet("/app.js")]
    public IActionResult AppJs() => QuoterScript("app.js");

    private IActionResult QuoterScript(string file)
    {
        var path = Path.Combine(_env.ContentRootPath, "Quoter", file);
        if (!System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, "application/javascript; charset=utf-8");
    }
}
