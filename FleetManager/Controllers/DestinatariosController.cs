using FleetManager.Models;
using FleetManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FleetManager.Controllers;

/// <summary>
/// Email recipients maintainer (admin only) and sending the quote proposal by
/// email to a saved recipient. Uses the server's SMTP/Resend configuration
/// (IServicioCorreo), the same one the mixer report uses.
/// </summary>
[Authorize]
public class DestinatariosController : Controller
{
    private readonly ISeguridadRepositorio _repo;
    private readonly IServicioCorreo _correo;
    public DestinatariosController(ISeguridadRepositorio repo, IServicioCorreo correo)
    {
        _repo = repo; _correo = correo;
    }

    // GET /recipients -> maintainer page (managing recipients is admin-only)
    [HttpGet("/recipients")]
    [Authorize(Roles = IdentidadApp.RolAdmin)]
    public IActionResult Index() => View();

    // GET /api/recipients -> list (used by the maintainer and by the quoter dropdown)
    [HttpGet("/api/recipients")]
    [Authorize(Policy = "Quotes")]
    public async Task<IActionResult> Listar(CancellationToken ct) => Json(await _repo.ListarDestinatariosAsync(ct));

    [HttpPost("/api/recipients")]
    [Authorize(Roles = IdentidadApp.RolAdmin)]
    public async Task<IActionResult> Crear([FromBody] RecipientRequest req, CancellationToken ct)
        => Responder(await _repo.CrearDestinatarioAsync(req ?? new(), Usuario(), HttpContext.Ip(), ct));

    [HttpPost("/api/recipients/update")]
    [Authorize(Roles = IdentidadApp.RolAdmin)]
    public async Task<IActionResult> Actualizar([FromBody] RecipientRequest req, CancellationToken ct)
        => Responder(await _repo.ActualizarDestinatarioAsync(req ?? new(), Usuario(), HttpContext.Ip(), ct));

    [HttpPost("/api/recipients/delete")]
    [Authorize(Roles = IdentidadApp.RolAdmin)]
    public async Task<IActionResult> Eliminar([FromBody] RecipientRequest req, CancellationToken ct)
        => Responder(await _repo.EliminarDestinatarioAsync(req?.IdRecipient ?? 0, Usuario(), HttpContext.Ip(), ct));

    // POST /api/quote/email -> send the current proposal to saved recipients (To + CC)
    [HttpPost("/api/quote/email")]
    [Authorize(Policy = "Quotes")]
    public async Task<IActionResult> EnviarCotizacion([FromBody] QuoteEmailRequest req, CancellationToken ct)
    {
        if (req == null) return BadRequest(Resultado.Mal("Select a recipient."));
        if (!_correo.Configurado)
            return BadRequest(Resultado.Mal("Email is not configured on the server. Set SMTP_HOST/SMTP_USER/SMTP_PASS/MAIL_FROM or RESEND_API_KEY."));

        // To ids (accept the list and the legacy single id).
        var toIds = (req.RecipientIds ?? new()).ToList();
        if (req.RecipientId is > 0) toIds.Add(req.RecipientId.Value);
        toIds = toIds.Where(x => x > 0).Distinct().ToList();
        if (toIds.Count == 0) return BadRequest(Resultado.Mal("Select at least one recipient."));

        // CC ids (never duplicate a To recipient).
        var ccIds = (req.CcIds ?? new()).Where(x => x > 0 && !toIds.Contains(x)).Distinct().ToList();

        var to = await ResolverCorreosAsync(toIds, ct);
        if (to.Count == 0) return BadRequest(Resultado.Mal("Recipient not found."));
        var cc = await ResolverCorreosAsync(ccIds, ct);

        var asunto = string.IsNullOrWhiteSpace(req.Subject) ? "JCK Sand Pit — Quote" : req.Subject!.Trim();
        var html = string.IsNullOrWhiteSpace(req.Html) ? "<p>Quote attached.</p>" : req.Html!;
        var texto = "Please see the attached quote proposal.";

        // Optional PDF attachment produced by the browser from the proposal.
        List<AdjuntoCorreo>? adjuntos = null;
        if (!string.IsNullOrWhiteSpace(req.PdfBase64))
        {
            try
            {
                var bytes = Convert.FromBase64String(req.PdfBase64!);
                if (bytes.Length > 15_000_000) return BadRequest(Resultado.Mal("The PDF is too large (max 15 MB)."));
                var nombre = string.IsNullOrWhiteSpace(req.PdfName) ? "JCK_Quote.pdf" : req.PdfName!.Trim();
                if (!nombre.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) nombre += ".pdf";
                adjuntos = new() { new AdjuntoCorreo(nombre, bytes, "application/pdf") };
            }
            catch (FormatException) { return BadRequest(Resultado.Mal("The attached PDF could not be read.")); }
        }

        var (ok, error) = await _correo.EnviarAsync(to, cc, Array.Empty<string>(),
            asunto, html, texto, ct, adjuntos);
        return ok
            ? Json(Resultado.Bien($"Email sent to {string.Join(", ", to)}" + (cc.Count > 0 ? $" (cc: {string.Join(", ", cc)})" : "") + "."))
            : BadRequest(Resultado.Mal(error ?? "Could not send the email."));
    }

    private async Task<List<string>> ResolverCorreosAsync(List<long> ids, CancellationToken ct)
    {
        var correos = new List<string>();
        foreach (var id in ids)
        {
            var e = await _repo.ObtenerDestinatarioEmailAsync(id, ct);
            if (e != null) correos.Add(e);
        }
        return correos;
    }

    private IActionResult Responder(Resultado r) => r.Ok ? Json(r) : BadRequest(r);
    private string Usuario() => User.Usuario()?.Username ?? "unknown";
}
