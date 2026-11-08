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
[Authorize(Roles = IdentidadApp.RolAdmin)]
public class DestinatariosController : Controller
{
    private readonly ISeguridadRepositorio _repo;
    private readonly IServicioCorreo _correo;
    public DestinatariosController(ISeguridadRepositorio repo, IServicioCorreo correo)
    {
        _repo = repo; _correo = correo;
    }

    // GET /recipients -> maintainer page
    [HttpGet("/recipients")]
    public IActionResult Index() => View();

    // GET /api/recipients -> list (used by the maintainer and by the quoter dropdown)
    [HttpGet("/api/recipients")]
    public async Task<IActionResult> Listar(CancellationToken ct) => Json(await _repo.ListarDestinatariosAsync(ct));

    [HttpPost("/api/recipients")]
    public async Task<IActionResult> Crear([FromBody] RecipientRequest req, CancellationToken ct)
        => Responder(await _repo.CrearDestinatarioAsync(req ?? new(), Usuario(), HttpContext.Ip(), ct));

    [HttpPost("/api/recipients/update")]
    public async Task<IActionResult> Actualizar([FromBody] RecipientRequest req, CancellationToken ct)
        => Responder(await _repo.ActualizarDestinatarioAsync(req ?? new(), Usuario(), HttpContext.Ip(), ct));

    [HttpPost("/api/recipients/delete")]
    public async Task<IActionResult> Eliminar([FromBody] RecipientRequest req, CancellationToken ct)
        => Responder(await _repo.EliminarDestinatarioAsync(req?.IdRecipient ?? 0, Usuario(), HttpContext.Ip(), ct));

    // POST /api/quote/email -> send the current proposal to a saved recipient
    [HttpPost("/api/quote/email")]
    public async Task<IActionResult> EnviarCotizacion([FromBody] QuoteEmailRequest req, CancellationToken ct)
    {
        if (req?.RecipientId is not > 0) return BadRequest(Resultado.Mal("Select a recipient."));
        if (!_correo.Configurado)
            return BadRequest(Resultado.Mal("Email is not configured on the server. Set SMTP_HOST/SMTP_USER/SMTP_PASS/MAIL_FROM or RESEND_API_KEY."));

        var email = await _repo.ObtenerDestinatarioEmailAsync(req.RecipientId!.Value, ct);
        if (email == null) return BadRequest(Resultado.Mal("Recipient not found."));

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

        var (ok, error) = await _correo.EnviarAsync(new[] { email }, Array.Empty<string>(), Array.Empty<string>(),
            asunto, html, texto, ct, adjuntos);
        return ok
            ? Json(Resultado.Bien($"Email sent to {email}."))
            : BadRequest(Resultado.Mal(error ?? "Could not send the email."));
    }

    private IActionResult Responder(Resultado r) => r.Ok ? Json(r) : BadRequest(r);
    private string Usuario() => User.Usuario()?.Username ?? "unknown";
}
