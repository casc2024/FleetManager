using FleetManager.Models;
using Npgsql;

namespace FleetManager.Services;

/// <summary>
/// Fleet board access over PostgreSQL (schema "fleet"). Rebuilt from the
/// Fleet_Manager_v15 design: equipment, plants, drivers, plant-per-truck,
/// daily performance, snapshots and a change log. Each change is written to
/// fleet.change_log with the signed-in user and client IP.
/// </summary>
public class FlotaRepositorioPostgres : IFlotaRepositorio
{
    private readonly string _cadenaConexion;
    private readonly ILogger<FlotaRepositorioPostgres> _log;

    public FlotaRepositorioPostgres(IConfiguration config, ILogger<FlotaRepositorioPostgres> log)
    {
        _cadenaConexion = ConstruirCadenaConexion(config);
        _log = log;
    }

    // ------------------------------------------------------------------
    // Connection string: Railway exposes DATABASE_URL (postgresql://user:pass@host:port/db)
    // Shared by the other repositories (mezcladoras, seguridad).
    // ------------------------------------------------------------------
    public static string ConstruirCadenaConexion(IConfiguration config)
    {
        var valor = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(valor))
            valor = config.GetConnectionString("Flota");
        if (string.IsNullOrWhiteSpace(valor))
            throw new InvalidOperationException("Defina la variable DATABASE_URL o ConnectionStrings:Flota en appsettings.json.");

        valor = valor.Trim();
        if (!valor.StartsWith("postgres", StringComparison.OrdinalIgnoreCase) || !valor.Contains("://"))
            return valor;

        var uri = new Uri(valor);
        var userInfo = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
            Database = uri.AbsolutePath.TrimStart('/'),
            SslMode = SslMode.Prefer,
            Pooling = true
        };
        return b.ConnectionString;
    }

    private async Task<NpgsqlConnection> AbrirAsync(CancellationToken ct)
    {
        var cn = new NpgsqlConnection(_cadenaConexion);
        await cn.OpenAsync(ct);
        return cn;
    }

    private static string Norm(string? s) => (s ?? "").Trim();
    private static object NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s.Trim();

    private static async Task LogAsync(NpgsqlConnection cn, NpgsqlTransaction tx, string? equipo, string campo,
        string? anterior, string? nuevo, string usuario, string? ip, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO fleet.change_log (equipment, field, old_value, new_value, changed_by, changed_ip)
            VALUES (@e, @f, @o, @n, @u, NULLIF(@ip,'')::inet)", cn, tx);
        cmd.Parameters.AddWithValue("e", (object?)equipo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("f", campo);
        cmd.Parameters.AddWithValue("o", (object?)anterior ?? DBNull.Value);
        cmd.Parameters.AddWithValue("n", (object?)nuevo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("u", usuario);
        cmd.Parameters.AddWithValue("ip", ip ?? "");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------
    public async Task VerificarConexionAsync(CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM fleet.equipment", cn);
        var n = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        _log.LogInformation("Fleet board connected. Equipment: {n}", n);
    }

    // ------------------------------------------------------------------
    // Read
    // ------------------------------------------------------------------
    public async Task<DatosTablero> ObtenerDatosAsync(CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        var d = new DatosTablero();

        await using (var cmd = new NpgsqlCommand(@"
            SELECT equipment_number, grp, truck_number, location, truck_plant, status, load_status, updated_by, updated_at
            FROM fleet.equipment ORDER BY grp, equipment_number", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                d.Equipos.Add(new Equipo
                {
                    NumeroEquipo = rd.GetString(0),
                    Grupo = rd.GetString(1),
                    NumeroCamion = rd.IsDBNull(2) ? null : rd.GetString(2),
                    Ubicacion = rd.IsDBNull(3) ? null : rd.GetString(3),
                    PlantaCamion = rd.IsDBNull(4) ? null : rd.GetString(4),
                    Estado = rd.GetString(5),
                    Carga = rd.GetString(6),
                    ActualizadoPor = rd.IsDBNull(7) ? null : rd.GetString(7),
                    FechaActualizacion = rd.GetDateTime(8)
                });

        await using (var cmd = new NpgsqlCommand("SELECT code FROM fleet.plant ORDER BY code", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) d.Plantas.Add(rd.GetString(0));

        await using (var cmd = new NpgsqlCommand(
            "SELECT id_driver, name, plant, truck FROM fleet.driver WHERE is_active ORDER BY lower(name)", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                d.Conductores.Add(new Conductor
                {
                    IdConductor = rd.GetInt64(0),
                    Nombre = rd.GetString(1),
                    Planta = rd.IsDBNull(2) ? null : rd.GetString(2),
                    Camion = rd.IsDBNull(3) ? null : rd.GetString(3)
                });

        await using (var cmd = new NpgsqlCommand(@"
            SELECT equipment, field, old_value, new_value, changed_by, changed_at
            FROM fleet.change_log ORDER BY changed_at DESC, id DESC LIMIT 60", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                d.Historial.Add(new HistorialItem
                {
                    NumeroEquipo = rd.IsDBNull(0) ? "" : rd.GetString(0),
                    Campo = rd.GetString(1),
                    ValorAnterior = rd.IsDBNull(2) ? null : rd.GetString(2),
                    ValorNuevo = rd.IsDBNull(3) ? null : rd.GetString(3),
                    Usuario = rd.IsDBNull(4) ? "" : rd.GetString(4),
                    Fecha = rd.GetDateTime(5)
                });

        await using (var cmd = new NpgsqlCommand(@"
            SELECT snap_date, total, ok, down, availability, cement, ash, empty
            FROM fleet.snapshot ORDER BY snap_date DESC, id_snapshot DESC LIMIT 52", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                d.Snapshots.Add(new SnapshotItem
                {
                    Fecha = rd.GetDateTime(0),
                    Total = rd.GetInt32(1),
                    Ok = rd.GetInt32(2),
                    Down = rd.GetInt32(3),
                    Disponibilidad = rd.GetDecimal(4),
                    Cement = rd.GetInt32(5),
                    Ash = rd.GetInt32(6),
                    Empty = rd.GetInt32(7)
                });

        return d;
    }

    // ------------------------------------------------------------------
    // Save a board row
    // ------------------------------------------------------------------
    public async Task<Resultado> GuardarFilaAsync(GuardarFilaRequest req, string usuario, string? ip, CancellationToken ct = default)
    {
        var eq = Norm(req.NumeroEquipo);
        if (eq.Length == 0) return Resultado.Mal("Equipment Number is required.");

        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        // Current values
        string grupo, truckNo, loc, plant, status, load;
        await using (var cmd = new NpgsqlCommand(
            "SELECT grp, truck_number, location, truck_plant, status, load_status FROM fleet.equipment WHERE equipment_number = @e FOR UPDATE", cn, tx))
        {
            cmd.Parameters.AddWithValue("e", eq);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Resultado.Mal("Equipment not found.");
            grupo = rd.GetString(0);
            truckNo = rd.IsDBNull(1) ? "" : rd.GetString(1);
            loc = rd.IsDBNull(2) ? "" : rd.GetString(2);
            plant = rd.IsDBNull(3) ? "" : rd.GetString(3);
            status = rd.GetString(4);
            load = rd.GetString(5);
        }

        var esCamion = grupo.StartsWith("TRUCK", StringComparison.OrdinalIgnoreCase);
        // Next values. Trucks keep their truck_number (auto pairing) and set truck_plant;
        // trailers set truck_number and never have truck_plant.
        var nTruck = esCamion ? truckNo : Norm(req.Camion);
        var nLoc = Norm(req.Ubicacion);
        var nPlant = esCamion ? Norm(req.PlantaCamion) : "";
        var nStatus = Norm(req.Estado);
        var nLoad = Norm(req.Carga);
        if (nLoad.Length == 0) nLoad = "EMPTY";

        var cambios = new List<(string campo, string? ant, string? nue)>();

        // If a trailer takes a truck already pulled by another trailer, release the other.
        if (!esCamion && nTruck.Length > 0 && !nTruck.Equals(truckNo, StringComparison.OrdinalIgnoreCase))
        {
            await using var cmd = new NpgsqlCommand(@"
                UPDATE fleet.equipment SET truck_number = NULL, updated_by = @u, updated_at = CURRENT_TIMESTAMP
                WHERE grp LIKE 'TRAILER%' AND truck_number = @t AND equipment_number <> @e
                RETURNING equipment_number", cn, tx);
            cmd.Parameters.AddWithValue("u", usuario);
            cmd.Parameters.AddWithValue("t", nTruck);
            cmd.Parameters.AddWithValue("e", eq);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            var liberados = new List<string>();
            while (await rd.ReadAsync(ct)) liberados.Add(rd.GetString(0));
            await rd.CloseAsync();
            foreach (var otro in liberados)
                await LogAsync(cn, tx, otro, "Truck", nTruck, "", usuario, ip, ct);
        }

        if ((truckNo ?? "") != nTruck) cambios.Add(("Truck", truckNo, nTruck));
        if ((loc ?? "") != nLoc) cambios.Add(("Location", loc, nLoc));
        if (esCamion && (plant ?? "") != nPlant) cambios.Add(("Plant", plant, nPlant));
        if ((status ?? "") != nStatus) cambios.Add(("Status", status, nStatus));
        if ((load ?? "") != nLoad) cambios.Add(("Load", load, nLoad));

        if (cambios.Count == 0) { await tx.CommitAsync(ct); return Resultado.Bien("No changes."); }

        await using (var cmd = new NpgsqlCommand(@"
            UPDATE fleet.equipment
               SET truck_number = @t, location = @l, truck_plant = @p,
                   status = @s, load_status = @ld, updated_by = @u, updated_at = CURRENT_TIMESTAMP
             WHERE equipment_number = @e", cn, tx))
        {
            cmd.Parameters.AddWithValue("t", NullIfEmpty(nTruck));
            cmd.Parameters.AddWithValue("l", NullIfEmpty(nLoc));
            cmd.Parameters.AddWithValue("p", NullIfEmpty(nPlant));
            cmd.Parameters.AddWithValue("s", nStatus);
            cmd.Parameters.AddWithValue("ld", nLoad);
            cmd.Parameters.AddWithValue("u", usuario);
            cmd.Parameters.AddWithValue("e", eq);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var (campo, ant, nue) in cambios)
            await LogAsync(cn, tx, eq, campo, ant, nue, usuario, ip, ct);

        await tx.CommitAsync(ct);
        return Resultado.Bien("Saved.");
    }

    // ------------------------------------------------------------------
    // Equipment administration
    // ------------------------------------------------------------------
    private static readonly string[] Grupos =
        { "TRUCK T", "TRUCK CT", "TRUCK BUMPER", "TRAILER T", "TRAILER CT", "TRAILER BUMPER" };

    public async Task<Resultado> AgregarEquipoAsync(EquipoRequest req, string usuario, string? ip, CancellationToken ct = default)
    {
        var eq = Norm(req.NumeroEquipo);
        var grupo = Norm(req.Grupo).ToUpperInvariant();
        if (eq.Length == 0) return Resultado.Mal("Enter an equipment number.");
        if (!Grupos.Contains(grupo)) return Resultado.Mal("Choose a valid equipment type.");

        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        await using (var chk = new NpgsqlCommand("SELECT 1 FROM fleet.equipment WHERE lower(equipment_number) = lower(@e)", cn, tx))
        {
            chk.Parameters.AddWithValue("e", eq);
            if (await chk.ExecuteScalarAsync(ct) != null) return Resultado.Mal("Equipment already exists.");
        }

        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO fleet.equipment (equipment_number, grp, status, load_status, updated_by)
            VALUES (@e, @g, '', 'EMPTY', @u)", cn, tx))
        {
            cmd.Parameters.AddWithValue("e", eq);
            cmd.Parameters.AddWithValue("g", grupo);
            cmd.Parameters.AddWithValue("u", usuario);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(cn, tx, eq, "Equipment added", null, grupo, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Added.");
    }

    public async Task<Resultado> EliminarEquipoAsync(string numeroEquipo, string usuario, string? ip, CancellationToken ct = default)
    {
        var eq = Norm(numeroEquipo);
        if (eq.Length == 0) return Resultado.Mal("Select equipment to remove.");

        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        string grupo;
        await using (var cmd = new NpgsqlCommand("SELECT grp FROM fleet.equipment WHERE equipment_number = @e FOR UPDATE", cn, tx))
        {
            cmd.Parameters.AddWithValue("e", eq);
            var g = await cmd.ExecuteScalarAsync(ct);
            if (g == null) return Resultado.Mal("Equipment not found.");
            grupo = (string)g;
        }

        var esCamion = grupo.StartsWith("TRUCK", StringComparison.OrdinalIgnoreCase);
        if (esCamion)
        {
            await using (var c1 = new NpgsqlCommand(
                "UPDATE fleet.equipment SET truck_number = NULL, updated_by = @u, updated_at = CURRENT_TIMESTAMP WHERE grp LIKE 'TRAILER%' AND truck_number = @e", cn, tx))
            { c1.Parameters.AddWithValue("u", usuario); c1.Parameters.AddWithValue("e", eq); await c1.ExecuteNonQueryAsync(ct); }
            await using (var c2 = new NpgsqlCommand(
                "UPDATE fleet.driver SET truck = NULL, updated_by = @u, updated_at = CURRENT_TIMESTAMP WHERE truck = @e", cn, tx))
            { c2.Parameters.AddWithValue("u", usuario); c2.Parameters.AddWithValue("e", eq); await c2.ExecuteNonQueryAsync(ct); }
        }

        await using (var del = new NpgsqlCommand("DELETE FROM fleet.equipment WHERE equipment_number = @e", cn, tx))
        { del.Parameters.AddWithValue("e", eq); await del.ExecuteNonQueryAsync(ct); }

        await LogAsync(cn, tx, eq, "Equipment removed", grupo, "Removed", usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Equipment removed.");
    }

    // ------------------------------------------------------------------
    // Plants
    // ------------------------------------------------------------------
    public async Task<Resultado> AgregarPlantaAsync(string codigo, string usuario, string? ip, CancellationToken ct = default)
    {
        var p = Norm(codigo).ToUpperInvariant();
        if (p.Length == 0) return Resultado.Mal("Enter a plant code.");
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO fleet.plant (code, created_by) VALUES (@c, @u) ON CONFLICT (code) DO NOTHING", cn);
        cmd.Parameters.AddWithValue("c", p);
        cmd.Parameters.AddWithValue("u", usuario);
        var n = await cmd.ExecuteNonQueryAsync(ct);
        return n > 0 ? Resultado.Bien("Plant added.") : Resultado.Mal("Plant already exists.");
    }

    public async Task<Resultado> EliminarPlantaAsync(string codigo, string usuario, string? ip, CancellationToken ct = default)
    {
        var p = Norm(codigo);
        if (p.Length == 0) return Resultado.Mal("Select a plant.");
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        await using (var c1 = new NpgsqlCommand(
            "UPDATE fleet.equipment SET location = NULL WHERE location = @p", cn, tx))
        { c1.Parameters.AddWithValue("p", p); await c1.ExecuteNonQueryAsync(ct); }
        await using (var c2 = new NpgsqlCommand(
            "UPDATE fleet.equipment SET truck_plant = NULL WHERE truck_plant = @p", cn, tx))
        { c2.Parameters.AddWithValue("p", p); await c2.ExecuteNonQueryAsync(ct); }
        await using (var c3 = new NpgsqlCommand(
            "UPDATE fleet.driver SET plant = NULL WHERE plant = @p", cn, tx))
        { c3.Parameters.AddWithValue("p", p); await c3.ExecuteNonQueryAsync(ct); }
        await using (var del = new NpgsqlCommand("DELETE FROM fleet.plant WHERE code = @p", cn, tx))
        { del.Parameters.AddWithValue("p", p); await del.ExecuteNonQueryAsync(ct); }

        await LogAsync(cn, tx, null, "Plant removed", p, "Removed", usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Plant removed.");
    }

    // ------------------------------------------------------------------
    // Drivers
    // ------------------------------------------------------------------
    public async Task<Resultado> AgregarConductorAsync(ConductorFlotaRequest req, string usuario, string? ip, CancellationToken ct = default)
    {
        var name = Norm(req.Nombre);
        if (name.Length == 0) return Resultado.Mal("Enter a driver name.");
        await using var cn = await AbrirAsync(ct);
        await using (var chk = new NpgsqlCommand(
            "SELECT 1 FROM fleet.driver WHERE is_active AND lower(name) = lower(@n)", cn))
        {
            chk.Parameters.AddWithValue("n", name);
            if (await chk.ExecuteScalarAsync(ct) != null) return Resultado.Mal("Driver already exists.");
        }
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO fleet.driver (name, updated_by) VALUES (@n, @u) RETURNING id_driver", cn);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("u", usuario);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return Resultado.Bien("Driver added.", new { idDriver = id });
    }

    public async Task<Resultado> ActualizarConductorAsync(ConductorFlotaRequest req, string usuario, string? ip, CancellationToken ct = default)
    {
        if (req.IdConductor is not > 0) return Resultado.Mal("Driver not specified.");
        var camion = (req.Camion ?? "").Trim();
        await using var cn = await AbrirAsync(ct);

        // Business rule: a truck can belong to only one driver.
        if (camion.Length > 0)
        {
            await using var dup = new NpgsqlCommand(
                "SELECT name FROM fleet.driver WHERE is_active AND truck = @t AND id_driver <> @id LIMIT 1", cn);
            dup.Parameters.AddWithValue("t", camion);
            dup.Parameters.AddWithValue("id", req.IdConductor!.Value);
            var otro = await dup.ExecuteScalarAsync(ct) as string;
            if (otro != null) return Resultado.Mal($"Truck {camion} is already assigned to {otro}.");
        }

        await using var cmd = new NpgsqlCommand(@"
            UPDATE fleet.driver SET plant = @p, truck = @t, updated_by = @u, updated_at = CURRENT_TIMESTAMP
            WHERE id_driver = @id AND is_active", cn);
        cmd.Parameters.AddWithValue("p", NullIfEmpty(req.Planta));
        cmd.Parameters.AddWithValue("t", NullIfEmpty(req.Camion));
        cmd.Parameters.AddWithValue("u", usuario);
        cmd.Parameters.AddWithValue("id", req.IdConductor!.Value);
        var n = await cmd.ExecuteNonQueryAsync(ct);
        return n > 0 ? Resultado.Bien("Driver updated.") : Resultado.Mal("Driver not found.");
    }

    public async Task<Resultado> EliminarConductorAsync(long idConductor, string usuario, string? ip, CancellationToken ct = default)
    {
        if (idConductor <= 0) return Resultado.Mal("Driver not specified.");
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE fleet.driver SET is_active = FALSE, updated_by = @u, updated_at = CURRENT_TIMESTAMP WHERE id_driver = @id AND is_active", cn);
        cmd.Parameters.AddWithValue("u", usuario);
        cmd.Parameters.AddWithValue("id", idConductor);
        var n = await cmd.ExecuteNonQueryAsync(ct);
        return n > 0 ? Resultado.Bien("Driver removed.") : Resultado.Mal("Driver not found.");
    }

    // ------------------------------------------------------------------
    // Weekly snapshot
    // ------------------------------------------------------------------
    public async Task<SnapshotItem> CrearSnapshotAsync(string usuario, string? ip, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            WITH s AS (
                SELECT count(*) total,
                       count(*) FILTER (WHERE status='OK') ok,
                       count(*) FILTER (WHERE status='DOWN') down,
                       count(*) FILTER (WHERE load_status='CEMENT') cement,
                       count(*) FILTER (WHERE load_status='ASH') ash,
                       count(*) FILTER (WHERE load_status='EMPTY') empty
                FROM fleet.equipment)
            INSERT INTO fleet.snapshot (snap_date, total, ok, down, availability, cement, ash, empty, created_by)
            SELECT CURRENT_DATE, total, ok, down,
                   CASE WHEN total>0 THEN round(ok::numeric*100/total,2) ELSE 0 END,
                   cement, ash, empty, @u
            FROM s
            RETURNING snap_date, total, ok, down, availability, cement, ash, empty", cn);
        cmd.Parameters.AddWithValue("u", usuario);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await rd.ReadAsync(ct);
        return new SnapshotItem
        {
            Fecha = rd.GetDateTime(0),
            Total = rd.GetInt32(1),
            Ok = rd.GetInt32(2),
            Down = rd.GetInt32(3),
            Disponibilidad = rd.GetDecimal(4),
            Cement = rd.GetInt32(5),
            Ash = rd.GetInt32(6),
            Empty = rd.GetInt32(7)
        };
    }

    // ------------------------------------------------------------------
    // Daily truck performance
    // ------------------------------------------------------------------
    public async Task<List<PerfFila>> ListarPerfAsync(CancellationToken ct = default)
    {
        var lista = new List<PerfFila>();
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id_perf, to_char(perf_date,'YYYY-MM-DD'), perf_type, truck, trailer, driver, loads, revenue
            FROM fleet.performance ORDER BY perf_date DESC, perf_type, truck", cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lista.Add(new PerfFila
            {
                IdPerf = rd.GetInt64(0),
                Fecha = rd.GetString(1),
                Tipo = rd.GetString(2),
                Camion = rd.GetString(3),
                Remolque = rd.IsDBNull(4) ? null : rd.GetString(4),
                Conductor = rd.IsDBNull(5) ? null : rd.GetString(5),
                Cargas = rd.GetInt32(6),
                Ingreso = rd.GetDecimal(7)
            });
        return lista;
    }

    private static readonly string[] TiposPerf = { "PNEUMATIC", "END DUMP", "BUMPER TRAILER" };

    public async Task<Resultado> GuardarPerfAsync(PerfRequest req, string usuario, string? ip, CancellationToken ct = default)
    {
        var fecha = Norm(req.Fecha);
        var tipo = Norm(req.Tipo).ToUpperInvariant();
        var truck = Norm(req.Camion);
        if (fecha.Length == 0 || truck.Length == 0 || string.IsNullOrWhiteSpace(req.Conductor))
            return Resultado.Mal("Select date, truck and driver.");
        if (!TiposPerf.Contains(tipo)) return Resultado.Mal("Choose a valid truck type.");
        if (!DateTime.TryParse(fecha, out _)) return Resultado.Mal("Invalid date.");
        if (req.Cargas < 0 || req.Ingreso < 0) return Resultado.Mal("Enter valid loads and revenue.");

        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO fleet.performance (perf_date, perf_type, truck, trailer, driver, loads, revenue, updated_by)
            VALUES (@d::date, @ty, @tr, @trl, @dr, @ld, @rv, @u)
            ON CONFLICT (perf_date, perf_type, truck) DO UPDATE
               SET trailer = EXCLUDED.trailer, driver = EXCLUDED.driver,
                   loads = EXCLUDED.loads, revenue = EXCLUDED.revenue,
                   updated_by = EXCLUDED.updated_by, updated_at = CURRENT_TIMESTAMP", cn);
        cmd.Parameters.AddWithValue("d", fecha);
        cmd.Parameters.AddWithValue("ty", tipo);
        cmd.Parameters.AddWithValue("tr", truck);
        cmd.Parameters.AddWithValue("trl", NullIfEmpty(req.Remolque));
        cmd.Parameters.AddWithValue("dr", NullIfEmpty(req.Conductor));
        cmd.Parameters.AddWithValue("ld", req.Cargas);
        cmd.Parameters.AddWithValue("rv", req.Ingreso);
        cmd.Parameters.AddWithValue("u", usuario);
        await cmd.ExecuteNonQueryAsync(ct);
        return Resultado.Bien("Daily result saved.");
    }

    public async Task<Resultado> EliminarPerfAsync(long idPerf, string usuario, string? ip, CancellationToken ct = default)
    {
        if (idPerf <= 0) return Resultado.Mal("Entry not specified.");
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM fleet.performance WHERE id_perf = @id", cn);
        cmd.Parameters.AddWithValue("id", idPerf);
        var n = await cmd.ExecuteNonQueryAsync(ct);
        return n > 0 ? Resultado.Bien("Entry deleted.") : Resultado.Mal("Entry not found.");
    }
}
