using FleetManager.Models;

namespace FleetManager.Services;

/// <summary>Arma y envía el informe Overview (manual o programado).</summary>
public interface IServicioReporteMezcladoras
{
    Task<(string asunto, string html)> GenerarAsync(CancellationToken ct = default);
    Task<Resultado> EnviarAsync(string disparo, string usuario, string? ip, string? correoPrueba = null, CancellationToken ct = default);
    /// <summary>Hora local (según la zona configurada) a partir de un instante UTC.</summary>
    DateTime AHoraLocal(DateTime utc, string zona);
}

public class ServicioReporteMezcladoras : IServicioReporteMezcladoras
{
    private readonly IMezcladorasRepositorio _repo;
    private readonly IServicioCorreo _correo;
    private readonly IConfiguration _cfg;
    private readonly ILogger<ServicioReporteMezcladoras> _log;

    public ServicioReporteMezcladoras(IMezcladorasRepositorio repo, IServicioCorreo correo,
                                      IConfiguration cfg, ILogger<ServicioReporteMezcladoras> log)
    {
        _repo = repo; _correo = correo; _cfg = cfg; _log = log;
    }

    public DateTime AHoraLocal(DateTime utc, string zona)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(zona) ? "America/Chicago" : zona);
            return TimeZoneInfo.ConvertTimeFromUtc(utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime(), tz);
        }
        catch (TimeZoneNotFoundException) { return utc; }
        catch (InvalidTimeZoneException) { return utc; }
    }

    public async Task<(string asunto, string html)> GenerarAsync(CancellationToken ct = default)
    {
        var cfg = await _repo.ObtenerConfigReporteAsync(ct);
        var datos = await _repo.ObtenerDatosAsync(ct);
        var local = AHoraLocal(DateTime.UtcNow, cfg.ZonaHoraria);
        var url = Environment.GetEnvironmentVariable("APP_PUBLIC_URL") ?? _cfg["APP_PUBLIC_URL"];
        var html = ConstructorReporteOverview.Html(datos.Resumen, local, cfg.IncluirPlantas, cfg.IncluirCamiones,
                                                   datos.Camiones, string.IsNullOrWhiteSpace(url) ? null : url.TrimEnd('/') + "/mixer");
        return (ConstructorReporteOverview.Asunto(cfg.Asunto, datos.Resumen, local), html);
    }

    public async Task<Resultado> EnviarAsync(string disparo, string usuario, string? ip, string? correoPrueba = null, CancellationToken ct = default)
    {
        var cfg = await _repo.ObtenerConfigReporteAsync(ct);
        var datos = await _repo.ObtenerDatosAsync(ct);
        var local = AHoraLocal(DateTime.UtcNow, cfg.ZonaHoraria);

        List<string> to, cc, cco;
        if (!string.IsNullOrWhiteSpace(correoPrueba))
        {
            to = new List<string> { correoPrueba.Trim() }; cc = new(); cco = new();
        }
        else
        {
            var activos = cfg.Destinatarios.Where(d => d.Activo).ToList();
            to = activos.Where(d => d.Tipo == "TO").Select(d => d.Correo).ToList();
            cc = activos.Where(d => d.Tipo == "CC").Select(d => d.Correo).ToList();
            cco = activos.Where(d => d.Tipo == "BCC").Select(d => d.Correo).ToList();
            if (to.Count == 0 && cc.Count == 0 && cco.Count == 0)
                return Resultado.Mal("There are no active recipients configured");
        }

        var url = Environment.GetEnvironmentVariable("APP_PUBLIC_URL") ?? _cfg["APP_PUBLIC_URL"];
        var asunto = ConstructorReporteOverview.Asunto(cfg.Asunto, datos.Resumen, local);
        var html = ConstructorReporteOverview.Html(datos.Resumen, local, cfg.IncluirPlantas, cfg.IncluirCamiones,
                                                   datos.Camiones, string.IsNullOrWhiteSpace(url) ? null : url.TrimEnd('/') + "/mixer");
        var texto = ConstructorReporteOverview.TextoPlano(datos.Resumen, local);

        var (ok, error) = await _correo.EnviarAsync(to, cc, cco, asunto, html, texto, ct);
        var destinatarios = string.Join(", ", to.Concat(cc.Select(x => "cc:" + x)).Concat(cco.Select(x => "bcc:" + x)));

        try { await _repo.RegistrarEnvioAsync(disparo, destinatarios, asunto, ok, error, datos.Resumen, usuario, ip, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "No se pudo registrar el envío en la bitácora"); }

        return ok ? Resultado.Bien($"Report sent to {to.Count + cc.Count + cco.Count} recipient(s)")
                  : Resultado.Mal(error ?? "The report could not be sent");
    }
}

/// <summary>
/// Tarea en segundo plano: cada minuto revisa la configuración guardada en la base
/// y, si toca el horario, envía el informe. Se ejecuta dentro del mismo servicio web
/// de Railway (no hace falta un servicio aparte ni un cron externo).
///
/// Con varias réplicas, la reclamación del envío es atómica en la base
/// (UPDATE ... WHERE ultimo_envio &lt; ocurrencia), así que el correo sale una sola vez.
/// </summary>
public class TareaReporteProgramado : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<TareaReporteProgramado> _log;
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(1);

    public TareaReporteProgramado(IServiceProvider sp, ILogger<TareaReporteProgramado> log) { _sp = sp; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Environment.GetEnvironmentVariable("REPORTE_SCHEDULER") == "0")
        {
            _log.LogInformation("Tarea de informe programado desactivada por REPORTE_SCHEDULER=0");
            return;
        }

        _log.LogInformation("Tarea de informe programado iniciada (revisa cada minuto)");
        // Pequeña espera para no competir con el arranque
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Intervalo);
        do
        {
            try { await RevisarAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Fallo revisando el informe programado"); }
        }
        while (await SiguienteAsync(timer, stoppingToken));
    }

    private static async Task<bool> SiguienteAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task RevisarAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMezcladorasRepositorio>();
        var servicio = scope.ServiceProvider.GetRequiredService<IServicioReporteMezcladoras>();

        var cfg = await repo.ObtenerConfigReporteAsync(ct);
        if (!cfg.Activo) return;
        if (!TimeSpan.TryParse(cfg.HoraEnvio, out var hora)) return;

        var ahoraLocal = servicio.AHoraLocal(DateTime.UtcNow, cfg.ZonaHoraria);
        var dias = cfg.DiasSemana.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n is >= 1 and <= 7).ToHashSet();
        var diaIso = (int)ahoraLocal.DayOfWeek == 0 ? 7 : (int)ahoraLocal.DayOfWeek;   // ISO: 1=Lun … 7=Dom
        if (dias.Count > 0 && !dias.Contains(diaIso)) return;

        // ¿Ya pasó la hora de hoy?
        var programadoLocal = ahoraLocal.Date + hora;
        if (ahoraLocal < programadoLocal) return;
        // Ventana de 30 minutos: si el servicio estuvo dormido más que eso, no manda un informe viejo
        if (ahoraLocal - programadoLocal > TimeSpan.FromMinutes(30)) return;

        var ocurrenciaUtc = AUtc(programadoLocal, cfg.ZonaHoraria);
        if (!await repo.IntentarReclamarEnvioAsync(ocurrenciaUtc, ct)) return;   // ya se envió (o lo tomó otra réplica)

        _log.LogInformation("Enviando informe programado de las {hora} ({zona})", cfg.HoraEnvio, cfg.ZonaHoraria);
        var r = await servicio.EnviarAsync("PROGRAMADO", "Scheduler", null, null, ct);
        if (!r.Ok) _log.LogWarning("El informe programado no se pudo enviar: {msg}", r.Mensaje);
    }

    private static DateTime AUtc(DateTime local, string zona)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(zona) ? "America/Chicago" : zona);
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
        }
        catch { return DateTime.SpecifyKind(local, DateTimeKind.Utc); }
    }
}
