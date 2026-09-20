namespace FleetManager.Models;

// =====================================================================
//  NBR Ready Mix — Control diario de mezcladoras
//  DTOs del módulo /mixer (esquema PostgreSQL "mezcladoras")
// =====================================================================

public class CamionFila
{
    public long IdCamion { get; set; }
    public int Numero { get; set; }
    public string? Planta { get; set; }            // null = Unassigned
    public string Estado { get; set; } = "open";   // manned | down | open
    public string EstadoNombre { get; set; } = "";
    public string EstadoColor { get; set; } = "";
    public List<ConductorBreve> Conductores { get; set; } = new();
}

public class ConductorBreve
{
    public long IdConductor { get; set; }
    public string Nombre { get; set; } = "";
}

public class ConductorFila
{
    public long IdConductor { get; set; }
    public string Nombre { get; set; } = "";
    public string? Planta { get; set; }
    public int? NumeroCamion { get; set; }
}

public class OpcionMezcladoras
{
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string? ColorHex { get; set; }
}

public class HistorialMezcladoras
{
    public string Entidad { get; set; } = "";
    public string Referencia { get; set; } = "";
    public string Campo { get; set; } = "";
    public string? ValorAnterior { get; set; }
    public string? ValorNuevo { get; set; }
    public string Usuario { get; set; } = "";
    public DateTime Fecha { get; set; }
}

/// <summary>Configuración del envío programado del informe Overview.</summary>
public class ConfigReporte
{
    public bool Activo { get; set; }
    public string HoraEnvio { get; set; } = "07:00";       // HH:mm
    public string DiasSemana { get; set; } = "1,2,3,4,5";  // ISO 1=Lun … 7=Dom
    public string ZonaHoraria { get; set; } = "America/Chicago";
    public string Asunto { get; set; } = "NBR Ready Mix — Daily Fleet Status";
    public bool IncluirPlantas { get; set; } = true;
    public bool IncluirCamiones { get; set; }
    public DateTime? UltimoEnvio { get; set; }
    public List<DestinatarioReporte> Destinatarios { get; set; } = new();
    /// <summary>Solo lectura: indica si el servidor tiene configurado el envío de correo.</summary>
    public bool CorreoConfigurado { get; set; }
    public string? ProveedorCorreo { get; set; }
}

public class DestinatarioReporte
{
    public long IdDestinatario { get; set; }
    public string Correo { get; set; } = "";
    public string? Nombre { get; set; }
    public string Tipo { get; set; } = "TO";   // TO | CC | BCC
    public bool Activo { get; set; } = true;
}

public class EnvioReporte
{
    public DateTime Fecha { get; set; }
    public string Disparo { get; set; } = "";
    public string Destinatarios { get; set; } = "";
    public bool Exito { get; set; }
    public string? MensajeError { get; set; }
    public string Usuario { get; set; } = "";
}

/// <summary>Cifras del Overview; alimenta la página y el correo.</summary>
public class ResumenFlota
{
    public int TotalCamiones { get; set; }
    public int TotalConductores { get; set; }
    public int Manned { get; set; }
    public int Down { get; set; }
    public int OpenTrucks { get; set; }
    public DateTime? UltimaActualizacion { get; set; }
    public List<ResumenPlanta> Plantas { get; set; } = new();

    public int Pct(int valor) => TotalCamiones == 0 ? 0 : (int)Math.Round(valor * 100.0 / TotalCamiones);
}

public class ResumenPlanta
{
    public string Planta { get; set; } = "";
    public int TotalCamiones { get; set; }
    public int Manned { get; set; }
    public int Down { get; set; }
    public int OpenTrucks { get; set; }
    public int Conductores { get; set; }

    public int Pct(int valor) => TotalCamiones == 0 ? 0 : (int)Math.Round(valor * 100.0 / TotalCamiones);
}

public class DatosMezcladoras
{
    public List<CamionFila> Camiones { get; set; } = new();
    public List<ConductorFila> Conductores { get; set; } = new();
    public List<OpcionMezcladoras> Plantas { get; set; } = new();
    public List<OpcionMezcladoras> Estados { get; set; } = new();
    public ResumenFlota Resumen { get; set; } = new();
    public List<HistorialMezcladoras> Historial { get; set; } = new();
}

// ------------------------- Peticiones -------------------------

public class CamionRequest
{
    public int Numero { get; set; }
    public string? Planta { get; set; }
    public string? Estado { get; set; }
    public string? Usuario { get; set; }
}

public class ConductorRequest
{
    public long IdConductor { get; set; }
    public string? Nombre { get; set; }
    public string? Planta { get; set; }
    public int? NumeroCamion { get; set; }
    public string? Usuario { get; set; }
}

public class AsignacionRequest
{
    public int Numero { get; set; }
    public long IdConductor { get; set; }
    public string? Usuario { get; set; }
}

public class GuardarConfigRequest
{
    public ConfigReporte Config { get; set; } = new();
    public string? Usuario { get; set; }
}

public class EnvioRequest
{
    public string? Usuario { get; set; }
    /// <summary>Si viene, envía solo a este correo (prueba). Si no, usa los destinatarios configurados.</summary>
    public string? CorreoPrueba { get; set; }
}

public class Resultado
{
    public bool Ok { get; set; }
    public string Mensaje { get; set; } = "";
    public object? Datos { get; set; }

    public static Resultado Bien(string mensaje, object? datos = null) => new() { Ok = true, Mensaje = mensaje, Datos = datos };
    public static Resultado Mal(string mensaje) => new() { Ok = false, Mensaje = mensaje };
}
