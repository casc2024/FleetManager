namespace FleetManager.Models;

/// <summary>Fila del tablero (vista flota.v_tablero_flota).</summary>
public class EquipoFila
{
    public string NumeroEquipo { get; set; } = "";
    public string Grupo { get; set; } = "";          // TRAILER CT, TRAILER T, TRUCK CT, TRUCK T
    public bool EsCamion { get; set; }
    public string Familia { get; set; } = "";        // CT / T
    public string? NumeroCamion { get; set; }        // remolques: camión asignado
    public string? NumeroRemolque { get; set; }      // camiones: remolque que jala
    public string? Ubicacion { get; set; }
    public string? Estado { get; set; }
    public string? Carga { get; set; }
    public string ActualizadoPor { get; set; } = "";
    public DateTime FechaActualizacion { get; set; }
}

public class OpcionCatalogo
{
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string? ColorHex { get; set; }
}

public class Catalogos
{
    public List<OpcionCatalogo> Ubicaciones { get; set; } = new();
    public List<OpcionCatalogo> Estados { get; set; } = new();
    public List<OpcionCatalogo> Cargas { get; set; } = new();
    /// <summary>Camiones disponibles por familia: "CT" -> [1262, ...], "T" -> [300, ...]</summary>
    public Dictionary<string, List<string>> CamionesPorFamilia { get; set; } = new();
}

public class HistorialItem
{
    public string NumeroEquipo { get; set; } = "";
    public string Campo { get; set; } = "";
    public string? ValorAnterior { get; set; }
    public string? ValorNuevo { get; set; }
    public string Usuario { get; set; } = "";
    public DateTime Fecha { get; set; }
}

public class SnapshotItem
{
    public DateTime Fecha { get; set; }
    public int Total { get; set; }
    public int Ok { get; set; }
    public int Down { get; set; }
    public decimal Disponibilidad { get; set; }
    public int Cement { get; set; }
    public int Ash { get; set; }
    public int Empty { get; set; }
}

public class DatosTablero
{
    public List<EquipoFila> Equipos { get; set; } = new();
    public Catalogos Catalogos { get; set; } = new();
    public List<HistorialItem> Historial { get; set; } = new();
    public List<SnapshotItem> Snapshots { get; set; } = new();
}

/// <summary>Cuerpo del POST /api/guardar (botón Save de una fila).</summary>
public class GuardarFilaRequest
{
    public string NumeroEquipo { get; set; } = "";
    public string? Camion { get; set; }
    public string? Ubicacion { get; set; }
    public string? Estado { get; set; }
    public string? Carga { get; set; }
    public string? Usuario { get; set; }
}

public class UsuarioRequest
{
    public string? Usuario { get; set; }
}

public class ResultadoGuardar
{
    public bool Ok { get; set; }
    public int Cambios { get; set; }
    public string Mensaje { get; set; } = "";
}
