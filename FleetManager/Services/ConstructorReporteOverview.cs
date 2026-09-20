using System.Globalization;
using System.Net;
using System.Text;
using FleetManager.Models;

namespace FleetManager.Services;

/// <summary>
/// Arma el HTML del informe "Overview" que se envía por correo.
/// Usa tablas y estilos en línea porque los clientes de correo (Outlook, Gmail)
/// no soportan CSS externo, flexbox ni grid de forma confiable.
/// </summary>
public static class ConstructorReporteOverview
{
    private const string Navy = "#101d38";
    private const string Blue = "#165dff";
    private const string Purple = "#7759e8";
    private const string Green = "#18a874";
    private const string Red = "#e85353";
    private const string Amber = "#e99a2c";
    private const string Line = "#e7eaf0";
    private const string Muted = "#667085";

    public static string Asunto(string plantilla, ResumenFlota r, DateTime fechaLocal)
    {
        var texto = string.IsNullOrWhiteSpace(plantilla) ? "NBR Ready Mix — Daily Fleet Status" : plantilla;
        return texto
            .Replace("{fecha}", fechaLocal.ToString("ddd, MMM d", CultureInfo.InvariantCulture))
            .Replace("{date}", fechaLocal.ToString("ddd, MMM d", CultureInfo.InvariantCulture))
            .Replace("{manned}", r.Manned.ToString())
            .Replace("{down}", r.Down.ToString())
            .Replace("{open}", r.OpenTrucks.ToString())
            .Replace("{total}", r.TotalCamiones.ToString());
    }

    /// <summary>Versión en texto plano, para clientes que no muestran HTML.</summary>
    public static string TextoPlano(ResumenFlota r, DateTime fechaLocal)
    {
        var sb = new StringBuilder();
        sb.AppendLine("NBR READY MIX — DAILY FLEET STATUS");
        sb.AppendLine(fechaLocal.ToString("dddd, MMMM d, yyyy h:mm tt", CultureInfo.InvariantCulture));
        sb.AppendLine();
        sb.AppendLine($"Mixer Trucks : {r.TotalCamiones}");
        sb.AppendLine($"Drivers      : {r.TotalConductores}");
        sb.AppendLine($"Manned       : {r.Manned} ({r.Pct(r.Manned)}%)");
        sb.AppendLine($"Down         : {r.Down} ({r.Pct(r.Down)}%)");
        sb.AppendLine($"Open Trucks  : {r.OpenTrucks} ({r.Pct(r.OpenTrucks)}%)");
        sb.AppendLine();
        sb.AppendLine("PLANT OVERVIEW");
        foreach (var p in r.Plantas)
            sb.AppendLine($"  {p.Planta,-14} trucks {p.TotalCamiones,3} · manned {p.Manned,3} · down {p.Down,3} · open {p.OpenTrucks,3} · drivers {p.Conductores,3}");
        return sb.ToString();
    }

    public static string Html(ResumenFlota r, DateTime fechaLocal, bool incluirPlantas, bool incluirCamiones,
                              IEnumerable<CamionFila>? camiones = null, string? urlTablero = null)
    {
        var sb = new StringBuilder();
        sb.Append($@"<!DOCTYPE html><html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>NBR Ready Mix — Daily Fleet Status</title></head>
<body style=""margin:0;padding:0;background:#f5f7fb;"">
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f5f7fb;padding:24px 12px;font-family:'Segoe UI',Arial,Helvetica,sans-serif;"">
<tr><td align=""center"">
<table role=""presentation"" width=""640"" cellpadding=""0"" cellspacing=""0"" style=""width:640px;max-width:100%;background:#ffffff;border:1px solid {Line};border-radius:16px;overflow:hidden;"">

  <tr><td style=""background:{Navy};padding:22px 26px;color:#ffffff;"">
    <div style=""font-size:12px;letter-spacing:.12em;color:#9fb0d0;font-weight:700;"">NBR OPERATIONS</div>
    <div style=""font-size:24px;font-weight:800;margin-top:6px;"">Daily Fleet Status</div>
    <div style=""font-size:13px;color:#c9d2e6;margin-top:6px;"">{Esc(fechaLocal.ToString("dddd, MMMM d, yyyy · h:mm tt", CultureInfo.InvariantCulture))}</div>
  </td></tr>

  <tr><td style=""padding:22px 26px 6px;"">
    <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"">
      <tr>
        {Tarjeta("Mixer Trucks", r.TotalCamiones, "100% of fleet", Blue)}
        {Tarjeta("Drivers", r.TotalConductores, "assigned to the fleet", Purple)}
      </tr>
      <tr><td colspan=""2"" style=""height:12px;line-height:12px;"">&nbsp;</td></tr>
      <tr>
        {Tarjeta("Manned", r.Manned, r.Pct(r.Manned) + "% of fleet", Green)}
        {Tarjeta("Down", r.Down, r.Pct(r.Down) + "% of fleet", Red)}
      </tr>
      <tr><td colspan=""2"" style=""height:12px;line-height:12px;"">&nbsp;</td></tr>
      <tr>
        {Tarjeta("Open Trucks", r.OpenTrucks, r.Pct(r.OpenTrucks) + "% of fleet", Amber)}
        <td width=""50%"" style=""padding:0 0 0 6px;vertical-align:top;""></td>
      </tr>
    </table>
  </td></tr>

  <tr><td style=""padding:18px 26px 4px;"">
    <div style=""font-size:16px;font-weight:800;color:#182033;"">Fleet Distribution</div>
    <div style=""font-size:12px;color:{Muted};margin:4px 0 12px;"">Current percentage by status</div>
    {Barra(r)}
  </td></tr>");

        if (incluirPlantas && r.Plantas.Count > 0)
        {
            sb.Append($@"
  <tr><td style=""padding:18px 26px 4px;"">
    <div style=""font-size:16px;font-weight:800;color:#182033;"">Plant Overview</div>
    <div style=""font-size:12px;color:{Muted};margin:4px 0 12px;"">Trucks and drivers by plant</div>
    <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""border-collapse:collapse;font-size:13px;"">
      <tr style=""background:#fafbfc;"">
        <th align=""left""  style=""{Th}"">Plant</th>
        <th align=""right"" style=""{Th}"">Trucks</th>
        <th align=""right"" style=""{Th}color:{Green};"">Manned</th>
        <th align=""right"" style=""{Th}color:{Red};"">Down</th>
        <th align=""right"" style=""{Th}color:{Amber};"">Open</th>
        <th align=""right"" style=""{Th}"">Drivers</th>
      </tr>");
            foreach (var p in r.Plantas)
                sb.Append($@"
      <tr>
        <td style=""{Td}font-weight:700;color:#182033;"">{Esc(p.Planta)}</td>
        <td style=""{Td}"" align=""right"">{p.TotalCamiones}</td>
        <td style=""{Td}color:{Green};font-weight:700;"" align=""right"">{p.Manned}</td>
        <td style=""{Td}color:{Red};font-weight:700;"" align=""right"">{p.Down}</td>
        <td style=""{Td}color:{Amber};font-weight:700;"" align=""right"">{p.OpenTrucks}</td>
        <td style=""{Td}"" align=""right"">{p.Conductores}</td>
      </tr>");
            var sinPlanta = r.TotalCamiones - r.Plantas.Sum(p => p.TotalCamiones);
            if (sinPlanta > 0)
                sb.Append($@"
      <tr><td style=""{Td}color:{Muted};"">Unassigned</td><td style=""{Td}"" align=""right"">{sinPlanta}</td>
      <td style=""{Td}"" align=""right"">—</td><td style=""{Td}"" align=""right"">—</td><td style=""{Td}"" align=""right"">—</td><td style=""{Td}"" align=""right"">—</td></tr>");
            sb.Append(@"
    </table>
  </td></tr>");
        }

        if (incluirCamiones && camiones != null)
        {
            var lista = camiones.Where(c => c.Estado == "down").OrderBy(c => c.Numero).ToList();
            sb.Append($@"
  <tr><td style=""padding:18px 26px 4px;"">
    <div style=""font-size:16px;font-weight:800;color:#182033;"">Trucks Down ({lista.Count})</div>
    <div style=""font-size:12px;color:{Muted};margin:4px 0 12px;"">Units that need attention</div>");
            if (lista.Count == 0)
                sb.Append($@"<div style=""font-size:13px;color:{Muted};padding:10px 0;"">No trucks are down. 👍</div>");
            else
            {
                sb.Append($@"<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""border-collapse:collapse;font-size:13px;"">
      <tr style=""background:#fafbfc;""><th align=""left"" style=""{Th}"">Truck</th><th align=""left"" style=""{Th}"">Plant</th><th align=""left"" style=""{Th}"">Drivers</th></tr>");
                foreach (var c in lista)
                    sb.Append($@"<tr><td style=""{Td}font-weight:700;"">#{c.Numero}</td><td style=""{Td}"">{Esc(c.Planta ?? "Unassigned")}</td><td style=""{Td}color:{Muted};"">{Esc(c.Conductores.Count == 0 ? "No driver" : string.Join(", ", c.Conductores.Select(d => d.Nombre)))}</td></tr>");
                sb.Append("</table>");
            }
            sb.Append(@"
  </td></tr>");
        }

        if (!string.IsNullOrWhiteSpace(urlTablero))
            sb.Append($@"
  <tr><td style=""padding:22px 26px;"" align=""center"">
    <a href=""{Esc(urlTablero)}"" style=""display:inline-block;background:{Blue};color:#ffffff;text-decoration:none;font-weight:700;font-size:14px;padding:12px 22px;border-radius:10px;"">Open the control board</a>
  </td></tr>");

        sb.Append($@"
  <tr><td style=""padding:16px 26px 24px;border-top:1px solid {Line};color:{Muted};font-size:11px;"">
    Automatic report from NBR Ready Mix Operations Control.
  </td></tr>

</table>
</td></tr></table>
</body></html>");
        return sb.ToString();
    }

    private const string Th = "padding:9px 10px;border-bottom:1px solid #e7eaf0;font-size:10px;text-transform:uppercase;letter-spacing:.06em;color:#667085;";
    private const string Td = "padding:9px 10px;border-bottom:1px solid #f0f2f6;color:#182033;";

    private static string Tarjeta(string titulo, int valor, string pie, string color) => $@"
        <td width=""50%"" style=""padding:0 6px 0 0;vertical-align:top;"">
          <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""border:1px solid {Line};border-radius:14px;"">
            <tr><td style=""padding:16px 18px;"">
              <table role=""presentation"" width=""100%""><tr>
                <td style=""font-size:13px;color:{Muted};"">{Esc(titulo)}</td>
                <td align=""right""><span style=""display:inline-block;width:10px;height:10px;border-radius:50%;background:{color};""></span></td>
              </tr></table>
              <div style=""font-size:28px;font-weight:800;color:#182033;margin-top:8px;"">{valor}</div>
              <div style=""font-size:12px;color:{Muted};margin-top:3px;"">{Esc(pie)}</div>
            </td></tr>
          </table>
        </td>";

    /// <summary>Barra apilada 100% (sustituye al donut: los clientes de correo no soportan conic-gradient).</summary>
    private static string Barra(ResumenFlota r)
    {
        int m = r.Pct(r.Manned), d = r.Pct(r.Down), o = Math.Max(0, 100 - m - d);
        string Seg(int pct, string color) => pct <= 0 ? "" :
            $@"<td width=""{pct}%"" style=""background:{color};height:14px;line-height:14px;font-size:0;"">&nbsp;</td>";
        var vacio = (m + d + o) == 0 ? $@"<td width=""100%"" style=""background:#e7eaf0;height:14px;line-height:14px;font-size:0;"">&nbsp;</td>" : "";
        return $@"
    <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""border-radius:8px;overflow:hidden;"">
      <tr>{Seg(m, Green)}{Seg(d, Red)}{Seg(o, Amber)}{vacio}</tr>
    </table>
    <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin-top:10px;font-size:12px;color:{Muted};"">
      <tr>
        <td><span style=""color:{Green};"">●</span> Manned <b style=""color:#182033;"">{r.Manned}</b> · {m}%</td>
        <td align=""center""><span style=""color:{Red};"">●</span> Down <b style=""color:#182033;"">{r.Down}</b> · {d}%</td>
        <td align=""right""><span style=""color:{Amber};"">●</span> Open <b style=""color:#182033;"">{r.OpenTrucks}</b> · {r.Pct(r.OpenTrucks)}%</td>
      </tr>
    </table>";
    }

    private static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");
}
