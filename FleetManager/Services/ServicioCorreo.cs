using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace FleetManager.Services;

public interface IServicioCorreo
{
    bool Configurado { get; }
    string Proveedor { get; }
    /// <summary>Envía el correo. Devuelve (ok, mensajeDeError).</summary>
    Task<(bool ok, string? error)> EnviarAsync(IEnumerable<string> para, IEnumerable<string> cc, IEnumerable<string> cco,
                                               string asunto, string html, string textoPlano, CancellationToken ct = default);
}

/// <summary>
/// Envío de correo sin dependencias externas:
///   · Si existe RESEND_API_KEY usa la API HTTPS de Resend (recomendado en Railway:
///     no depende de puertos SMTP).
///   · Si no, usa SMTP con System.Net.Mail (Gmail, Outlook 365, SendGrid, Brevo, Mailgun…).
///
/// Variables de entorno:
///   SMTP_HOST, SMTP_PORT (587), SMTP_USER, SMTP_PASS, SMTP_SSL (true/false = STARTTLS),
///   MAIL_FROM ("NBR Ready Mix &lt;ops@dominio.com&gt;" o solo el correo), MAIL_FROM_NAME
///   RESEND_API_KEY (alternativa por API)
/// </summary>
public class ServicioCorreo : IServicioCorreo
{
    private readonly ILogger<ServicioCorreo> _log;
    private readonly IHttpClientFactory _http;

    private readonly string? _resendKey, _host, _usuario, _clave, _remitente, _nombreRemitente;
    private readonly int _puerto;
    private readonly bool _ssl;

    public ServicioCorreo(IConfiguration cfg, ILogger<ServicioCorreo> log, IHttpClientFactory http)
    {
        _log = log; _http = http;
        string? V(string k) => Environment.GetEnvironmentVariable(k) ?? cfg[k];

        _resendKey = V("RESEND_API_KEY");
        _host = V("SMTP_HOST");
        _puerto = int.TryParse(V("SMTP_PORT"), out var p) ? p : 587;
        _usuario = V("SMTP_USER");
        _clave = V("SMTP_PASS");
        _ssl = !string.Equals(V("SMTP_SSL"), "false", StringComparison.OrdinalIgnoreCase);
        _remitente = V("MAIL_FROM") ?? _usuario;
        _nombreRemitente = V("MAIL_FROM_NAME") ?? "NBR Ready Mix Operations";
    }

    public bool Configurado =>
        !string.IsNullOrWhiteSpace(_resendKey) ||
        (!string.IsNullOrWhiteSpace(_host) && !string.IsNullOrWhiteSpace(_remitente));

    public string Proveedor => !string.IsNullOrWhiteSpace(_resendKey) ? "Resend API"
        : !string.IsNullOrWhiteSpace(_host) ? $"SMTP {_host}:{_puerto}" : "sin configurar";

    public async Task<(bool ok, string? error)> EnviarAsync(IEnumerable<string> para, IEnumerable<string> cc, IEnumerable<string> cco,
        string asunto, string html, string textoPlano, CancellationToken ct = default)
    {
        var to = para.Where(EsCorreo).Distinct().ToList();
        var copia = cc.Where(EsCorreo).Distinct().ToList();
        var oculta = cco.Where(EsCorreo).Distinct().ToList();
        if (to.Count + copia.Count + oculta.Count == 0) return (false, "There are no recipients configured");
        if (!Configurado) return (false, "Email is not configured on the server (set SMTP_HOST/SMTP_USER/SMTP_PASS/MAIL_FROM or RESEND_API_KEY)");

        try
        {
            if (!string.IsNullOrWhiteSpace(_resendKey))
                return await EnviarPorResendAsync(to, copia, oculta, asunto, html, textoPlano, ct);
            return await EnviarPorSmtpAsync(to, copia, oculta, asunto, html, textoPlano, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error enviando el informe");
            return (false, ex.Message);
        }
    }

    // ------------------------------------------------------------------
    private async Task<(bool, string?)> EnviarPorSmtpAsync(List<string> to, List<string> cc, List<string> cco,
        string asunto, string html, string texto, CancellationToken ct)
    {
        using var mensaje = new MailMessage
        {
            From = new MailAddress(SoloCorreo(_remitente!), _nombreRemitente, Encoding.UTF8),
            Subject = asunto,
            SubjectEncoding = Encoding.UTF8,
            Body = texto,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false
        };
        mensaje.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(texto, Encoding.UTF8, "text/plain"));
        mensaje.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, "text/html"));
        foreach (var x in to) mensaje.To.Add(x);
        foreach (var x in cc) mensaje.CC.Add(x);
        foreach (var x in cco) mensaje.Bcc.Add(x);

        using var cliente = new SmtpClient(_host, _puerto)
        {
            EnableSsl = _ssl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 30000,
            Credentials = string.IsNullOrWhiteSpace(_usuario) ? null : new NetworkCredential(_usuario, _clave)
        };
        await cliente.SendMailAsync(mensaje, ct);
        _log.LogInformation("Informe enviado por SMTP a {n} destinatarios", to.Count + cc.Count + cco.Count);
        return (true, null);
    }

    // ------------------------------------------------------------------
    private async Task<(bool, string?)> EnviarPorResendAsync(List<string> to, List<string> cc, List<string> cco,
        string asunto, string html, string texto, CancellationToken ct)
    {
        var cuerpo = new Dictionary<string, object?>
        {
            ["from"] = _remitente!.Contains('<') ? _remitente : $"{_nombreRemitente} <{_remitente}>",
            ["to"] = to,
            ["subject"] = asunto,
            ["html"] = html,
            ["text"] = texto
        };
        if (cc.Count > 0) cuerpo["cc"] = cc;
        if (cco.Count > 0) cuerpo["bcc"] = cco;

        var cliente = _http.CreateClient();
        cliente.Timeout = TimeSpan.FromSeconds(30);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        req.Headers.Add("Authorization", "Bearer " + _resendKey);
        req.Content = new StringContent(JsonSerializer.Serialize(cuerpo), Encoding.UTF8, "application/json");

        using var resp = await cliente.SendAsync(req, ct);
        var contenido = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return (false, $"Resend {(int)resp.StatusCode}: {contenido}");
        _log.LogInformation("Informe enviado por Resend a {n} destinatarios", to.Count + cc.Count + cco.Count);
        return (true, null);
    }

    private static bool EsCorreo(string? s) => !string.IsNullOrWhiteSpace(s) && s.Contains('@') && s.Trim().Length > 3;

    private static string SoloCorreo(string remitente)
    {
        var i = remitente.IndexOf('<');
        return i >= 0 ? remitente[(i + 1)..].TrimEnd('>').Trim() : remitente.Trim();
    }
}
