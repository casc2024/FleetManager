namespace FleetManager.Models;

// =====================================================================
//  Fleet board model (schema "fleet") — rebuilt from the Fleet_Manager_v15
//  design: equipment (trucks & trailers of families T, CT, BUMPER), plants,
//  drivers, plant-per-truck, daily truck performance, snapshots and history.
// =====================================================================

/// <summary>A truck or trailer on the Fleet Control Board.</summary>
public class Equipo
{
    public string NumeroEquipo { get; set; } = "";     // equipment_number
    public string Grupo { get; set; } = "";             // TRUCK T|CT|BUMPER, TRAILER T|CT|BUMPER
    public string? NumeroCamion { get; set; }           // trailers: assigned truck
    public string? Ubicacion { get; set; }              // plant where the unit is
    public string? PlantaCamion { get; set; }           // trucks: assigned plant ("Plant (Trucks)")
    public string Estado { get; set; } = "";            // OK | DOWN | ""
    public string Carga { get; set; } = "EMPTY";        // CEMENT | ASH | EMPTY | LOADED
    public string? ActualizadoPor { get; set; }
    public DateTime FechaActualizacion { get; set; }
}

public class Conductor
{
    public long IdConductor { get; set; }
    public string Nombre { get; set; } = "";
    public string? Planta { get; set; }
    public string? Camion { get; set; }
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

/// <summary>Everything the board needs in one read.</summary>
public class DatosTablero
{
    public List<Equipo> Equipos { get; set; } = new();
    public List<string> Plantas { get; set; } = new();
    public List<Conductor> Conductores { get; set; } = new();
    public List<HistorialItem> Historial { get; set; } = new();
    public List<SnapshotItem> Snapshots { get; set; } = new();
}

// ----------------------------------------------------------------- requests
public class GuardarFilaRequest
{
    public string NumeroEquipo { get; set; } = "";
    public string? Camion { get; set; }          // trailers: assigned truck
    public string? Ubicacion { get; set; }
    public string? PlantaCamion { get; set; }    // trucks: assigned plant
    public string? Estado { get; set; }
    public string? Carga { get; set; }
}

public class EquipoRequest
{
    public string? NumeroEquipo { get; set; }
    public string? Grupo { get; set; }
}

public class PlantaRequest
{
    public string? Codigo { get; set; }
}

public class ConductorFlotaRequest
{
    public long? IdConductor { get; set; }
    public string? Nombre { get; set; }
    public string? Planta { get; set; }
    public string? Camion { get; set; }
}

// ----------------------------------------------------------------- performance
public class PerfFila
{
    public long IdPerf { get; set; }
    public string Fecha { get; set; } = "";       // yyyy-MM-dd
    public string Tipo { get; set; } = "";         // PNEUMATIC | END DUMP | BUMPER TRAILER
    public string Camion { get; set; } = "";
    public string? Remolque { get; set; }
    public string? Conductor { get; set; }
    public int Cargas { get; set; }
    public decimal Ingreso { get; set; }
}

public class PerfRequest
{
    public long? IdPerf { get; set; }
    public string? Fecha { get; set; }
    public string? Tipo { get; set; }
    public string? Camion { get; set; }
    public string? Remolque { get; set; }
    public string? Conductor { get; set; }
    public int Cargas { get; set; }
    public decimal Ingreso { get; set; }
}
