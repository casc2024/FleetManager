using System.Data;
using FleetManager.Models;
using Npgsql;

namespace FleetManager.Services;

/// <summary>
/// Implementación Npgsql del módulo de mezcladoras.
/// Cada escritura corre en una transacción que fija app.usuario / app.ip para
/// que los triggers de auditoría del esquema registren quién y desde dónde.
/// </summary>
public class MezcladorasRepositorioPostgres : IMezcladorasRepositorio
{
    private readonly string _cadenaConexion;
    private readonly ILogger<MezcladorasRepositorioPostgres> _log;

    public MezcladorasRepositorioPostgres(IConfiguration config, ILogger<MezcladorasRepositorioPostgres> log)
    {
        // Misma cadena que el resto de la app (DATABASE_URL o ConnectionStrings:Flota)
        _cadenaConexion = FlotaRepositorioPostgres.ConstruirCadenaConexion(config);
        _log = log;
    }

    private async Task<NpgsqlConnection> AbrirAsync(CancellationToken ct)
    {
        var cn = new NpgsqlConnection(_cadenaConexion);
        await cn.OpenAsync(ct);
        return cn;
    }

    private static async Task ContextoAsync(NpgsqlConnection cn, NpgsqlTransaction tx, string usuario, string? ip, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.usuario', @u, true), set_config('app.ip', @ip, true)", cn, tx);
        cmd.Parameters.AddWithValue("u", usuario);
        cmd.Parameters.AddWithValue("ip", (object?)ip ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task HistorialAsync(NpgsqlConnection cn, NpgsqlTransaction tx, string entidad, string referencia,
        string campo, string? anterior, string? nuevo, string usuario, string? ip, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO mezcladoras.historial_cambio (entidad, referencia, campo, valor_anterior, valor_nuevo, usuario, ip)
            VALUES (@e, @r, @c, @va, @vn, @u, NULLIF(@ip,'')::inet)", cn, tx);
        cmd.Parameters.AddWithValue("e", entidad);
        cmd.Parameters.AddWithValue("r", referencia);
        cmd.Parameters.AddWithValue("c", campo);
        cmd.Parameters.AddWithValue("va", (object?)anterior ?? DBNull.Value);
        cmd.Parameters.AddWithValue("vn", (object?)nuevo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("u", usuario);
        cmd.Parameters.AddWithValue("ip", ip ?? "");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? Vacio(string? s) =>
        string.IsNullOrWhiteSpace(s) || s.Trim().Equals("Unassigned", StringComparison.OrdinalIgnoreCase) ? null : s.Trim();

    // ------------------------------------------------------------------
    // Verificación al arrancar
    // ------------------------------------------------------------------
    public async Task VerificarConexionAsync(CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT total_camiones, total_conductores FROM mezcladoras.v_resumen_flota", cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (await rd.ReadAsync(ct))
            _log.LogInformation("Módulo mezcladoras conectado. Camiones: {c}, conductores: {d}", rd.GetInt64(0), rd.GetInt64(1));
    }

    // ------------------------------------------------------------------
    // Lectura
    // ------------------------------------------------------------------
    public async Task<DatosMezcladoras> ObtenerDatosAsync(CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        var datos = new DatosMezcladoras();

        await using (var cmd = new NpgsqlCommand(@"
            SELECT id_camion, numero, planta, estado, estado_nombre, estado_color
            FROM mezcladoras.v_camion ORDER BY numero", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
                datos.Camiones.Add(new CamionFila
                {
                    IdCamion = rd.GetInt64(0),
                    Numero = rd.GetInt32(1),
                    Planta = rd.IsDBNull(2) ? null : rd.GetString(2),
                    Estado = rd.GetString(3),
                    EstadoNombre = rd.GetString(4),
                    EstadoColor = rd.GetString(5)
                });
        }

        await using (var cmd = new NpgsqlCommand(@"
            SELECT d.id_conductor, d.nombre, p.codigo, c.numero, d.id_camion
            FROM mezcladoras.conductor d
            LEFT JOIN mezcladoras.planta p ON p.id_planta = d.id_planta
            LEFT JOIN mezcladoras.camion c ON c.id_camion = d.id_camion
            WHERE d.activo ORDER BY d.nombre", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                var c = new ConductorFila
                {
                    IdConductor = rd.GetInt64(0),
                    Nombre = rd.GetString(1),
                    Planta = rd.IsDBNull(2) ? null : rd.GetString(2),
                    NumeroCamion = rd.IsDBNull(3) ? null : rd.GetInt32(3)
                };
                datos.Conductores.Add(c);
                if (!rd.IsDBNull(4))
                {
                    var idCam = rd.GetInt64(4);
                    var camion = datos.Camiones.FirstOrDefault(x => x.IdCamion == idCam);
                    camion?.Conductores.Add(new ConductorBreve { IdConductor = c.IdConductor, Nombre = c.Nombre });
                }
            }
        }

        datos.Plantas = await CatalogoAsync(cn, "SELECT codigo, nombre, NULL FROM mezcladoras.planta WHERE activo ORDER BY orden, codigo", ct);
        datos.Estados = await CatalogoAsync(cn, "SELECT codigo, nombre, color_hex FROM mezcladoras.estado_camion WHERE activo ORDER BY orden", ct);
        datos.Resumen = await LeerResumenAsync(cn, ct);

        await using (var cmd = new NpgsqlCommand(@"
            SELECT entidad, referencia, campo, valor_anterior, valor_nuevo, usuario, fecha
            FROM mezcladoras.historial_cambio ORDER BY fecha DESC, id_historial DESC LIMIT 25", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
                datos.Historial.Add(new HistorialMezcladoras
                {
                    Entidad = rd.GetString(0),
                    Referencia = rd.GetString(1),
                    Campo = rd.GetString(2),
                    ValorAnterior = rd.IsDBNull(3) ? null : rd.GetString(3),
                    ValorNuevo = rd.IsDBNull(4) ? null : rd.GetString(4),
                    Usuario = rd.GetString(5),
                    Fecha = rd.GetDateTime(6)
                });
        }

        return datos;
    }

    public async Task<ResumenFlota> ObtenerResumenAsync(CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        return await LeerResumenAsync(cn, ct);
    }

    private static async Task<ResumenFlota> LeerResumenAsync(NpgsqlConnection cn, CancellationToken ct)
    {
        var r = new ResumenFlota();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT total_camiones, total_conductores, manned, down, open_trucks, ultima_actualizacion
            FROM mezcladoras.v_resumen_flota", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            if (await rd.ReadAsync(ct))
            {
                r.TotalCamiones = (int)rd.GetInt64(0);
                r.TotalConductores = (int)rd.GetInt64(1);
                r.Manned = (int)rd.GetInt64(2);
                r.Down = (int)rd.GetInt64(3);
                r.OpenTrucks = (int)rd.GetInt64(4);
                r.UltimaActualizacion = rd.IsDBNull(5) ? null : rd.GetDateTime(5);
            }
        }
        await using (var cmd = new NpgsqlCommand(@"
            SELECT planta, total_camiones, manned, down, open_trucks, conductores
            FROM mezcladoras.v_resumen_planta", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
                r.Plantas.Add(new ResumenPlanta
                {
                    Planta = rd.GetString(0),
                    TotalCamiones = (int)rd.GetInt64(1),
                    Manned = (int)rd.GetInt64(2),
                    Down = (int)rd.GetInt64(3),
                    OpenTrucks = (int)rd.GetInt64(4),
                    Conductores = (int)rd.GetInt64(5)
                });
        }
        return r;
    }

    private static async Task<List<OpcionMezcladoras>> CatalogoAsync(NpgsqlConnection cn, string sql, CancellationToken ct)
    {
        var lista = new List<OpcionMezcladoras>();
        await using var cmd = new NpgsqlCommand(sql, cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lista.Add(new OpcionMezcladoras
            {
                Codigo = rd.GetString(0),
                Nombre = rd.GetString(1),
                ColorHex = rd.IsDBNull(2) ? null : rd.GetString(2)
            });
        return lista;
    }

    // ------------------------------------------------------------------
    // Camiones
    // ------------------------------------------------------------------
    public async Task<Resultado> AgregarCamionAsync(CamionRequest req, string usuario, string? ip, CancellationToken ct = default)
    {
        if (req.Numero <= 0) return Resultado.Mal("Enter a valid truck number");

        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ContextoAsync(cn, tx, usuario, ip, ct);

        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM mezcladoras.camion WHERE numero = @n", cn, tx))
        {
            cmd.Parameters.AddWithValue("n", req.Numero);
            if (await cmd.ExecuteScalarAsync(ct) != null) return Resultado.Mal("This truck is already registered");
        }

        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO mezcladoras.camion (numero, id_planta, id_estado)
            VALUES (@n, (SELECT id_planta FROM mezcladoras.planta WHERE codigo = @p),
                        (SELECT id_estado FROM mezcladoras.estado_camion WHERE codigo = 'open'))", cn, tx))
        {
            cmd.Parameters.AddWithValue("n", req.Numero);
            cmd.Parameters.AddWithValue("p", (object?)Vacio(req.Planta) ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await HistorialAsync(cn, tx, "CAMION", "#" + req.Numero, "Alta", null, "Open Trucks", usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien($"Truck #{req.Numero} added");
    }

    // ------------------------------------------------------------------
    // Reglas de negocio camión ↔ conductor
    //   1. Solo un camión Manned puede tener conductores.
    //   2. Un camión Manned debe tener al menos un conductor.
    //      → al pasar a Manned se asigna el conductor en la misma transacción;
    //      → si un Manned se queda sin conductores, pasa a Open Trucks.
    //   3. Al pasar a Open Trucks o Down se liberan sus conductores.
    //   4. El conductor con camión hereda la planta del camión; si se le cambia
    //      la planta a otra distinta, deja el camión.
    // La base las valida también (triggers), esto da mensajes claros y
    // aplica los ajustes automáticos en una sola transacción.
    // ------------------------------------------------------------------

    private record CamionInfo(long Id, int Numero, string? Planta, string Estado);
    private record ConductorInfo(long Id, string Nombre);

    /// <summary>Ejecuta el trabajo en una transacción con contexto de auditoría.
    /// Si el trabajo devuelve error se revierte; si la base rechaza una regla
    /// (check_violation) se devuelve su mensaje.</summary>
    private async Task<Resultado> EnTransaccionAsync(string usuario, string? ip,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<Resultado>> trabajo, CancellationToken ct)
    {
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await ContextoAsync(cn, tx, usuario, ip, ct);
        try
        {
            var r = await trabajo(cn, tx);
            if (r.Ok) await tx.CommitAsync(ct); else await tx.RollbackAsync(ct);
            return r;
        }
        catch (PostgresException ex) when (ex.SqlState == "23514")
        {
            return Resultado.Mal(ex.MessageText);
        }
    }

    private static async Task<CamionInfo?> LeerCamionAsync(NpgsqlConnection cn, NpgsqlTransaction tx, int numero, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT c.id_camion, c.numero, p.codigo, e.codigo
            FROM mezcladoras.camion c
            JOIN mezcladoras.estado_camion e ON e.id_estado = c.id_estado
            LEFT JOIN mezcladoras.planta p ON p.id_planta = c.id_planta
            WHERE c.numero = @n FOR UPDATE OF c", cn, tx);
        cmd.Parameters.AddWithValue("n", numero);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new CamionInfo(rd.GetInt64(0), rd.GetInt32(1), rd.IsDBNull(2) ? null : rd.GetString(2), rd.GetString(3));
    }

    private static async Task<List<ConductorInfo>> ConductoresDeAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long idCamion, CancellationToken ct)
    {
        var lista = new List<ConductorInfo>();
        await using var cmd = new NpgsqlCommand(
            "SELECT id_conductor, nombre FROM mezcladoras.conductor WHERE id_camion = @id AND activo ORDER BY nombre", cn, tx);
        cmd.Parameters.AddWithValue("id", idCamion);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) lista.Add(new ConductorInfo(rd.GetInt64(0), rd.GetString(1)));
        return lista;
    }

    /// <summary>Regla 2: si el camión es Manned y ya no tiene conductores, pasa a Open Trucks.
    /// Devuelve el número del camión si cambió.</summary>
    private static async Task<int?> AbrirSiQuedaSinConductoresAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long idCamion,
        string usuario, string? ip, CancellationToken ct)
    {
        int numero;
        await using (var cmd = new NpgsqlCommand(@"
            UPDATE mezcladoras.camion c
               SET id_estado = (SELECT id_estado FROM mezcladoras.estado_camion WHERE codigo = 'open')
             WHERE c.id_camion = @id
               AND c.id_estado = (SELECT id_estado FROM mezcladoras.estado_camion WHERE codigo = 'manned')
               AND NOT EXISTS (SELECT 1 FROM mezcladoras.conductor d WHERE d.id_camion = c.id_camion AND d.activo)
         RETURNING c.numero", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", idCamion);
            var r = await cmd.ExecuteScalarAsync(ct);
            if (r is null or DBNull) return null;
            numero = Convert.ToInt32(r);
        }
        await HistorialAsync(cn, tx, "CAMION", "#" + numero, "Status", "manned", "open", usuario, ip, ct);
        return numero;
    }

    /// <summary>Regla 5: máximo de conductores por camión.</summary>
    public const int MaxConductoresPorCamion = 2;

    private static string MensajeCamionLleno(int numero) =>
        $"Truck #{numero} already has {MaxConductoresPorCamion} drivers (maximum). Remove one before assigning another.";

    private static async Task<int> ContarConductoresAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long idCamion, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM mezcladoras.conductor WHERE id_camion = @id AND activo", cn, tx);
        cmd.Parameters.AddWithValue("id", idCamion);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static string MensajeSoloManned(int numero, string estado) => estado == "down"
        ? $"Truck #{numero} is Down. Drivers can only be assigned to Manned trucks."
        : $"Truck #{numero} is Open. Set it to Manned in Mixer trucks (you'll choose the driver there) before assigning drivers.";

    /// <summary>Regla 1b: un camión Open Trucks que recibe un conductor pasa
    /// automáticamente a Manned. Un camión Down no puede recibir conductores.
    /// Se llama ANTES de asignar el conductor (el trigger exige Manned).
    /// Devuelve el aviso a mostrar, o null si no hubo cambio.</summary>
    private static async Task<(bool ok, string? aviso)> PrepararCamionParaConductorAsync(NpgsqlConnection cn, NpgsqlTransaction tx,
        CamionInfo cam, string usuario, string? ip, CancellationToken ct)
    {
        if (cam.Estado == "down") return (false, MensajeSoloManned(cam.Numero, cam.Estado));
        // Regla 5: máximo de conductores por camión
        if (await ContarConductoresAsync(cn, tx, cam.Id, ct) >= MaxConductoresPorCamion)
            return (false, MensajeCamionLleno(cam.Numero));
        if (cam.Estado == "manned") return (true, null);

        await using (var cmd = new NpgsqlCommand(@"
            UPDATE mezcladoras.camion
               SET id_estado = (SELECT id_estado FROM mezcladoras.estado_camion WHERE codigo = 'manned')
             WHERE id_camion = @id", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", cam.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await HistorialAsync(cn, tx, "CAMION", "#" + cam.Numero, "Status", cam.Estado, "manned", usuario, ip, ct);
        return (true, $"Truck #{cam.Numero} changed to Manned");
    }

    private static string Nombres(IEnumerable<ConductorInfo> lista) => string.Join(", ", lista.Select(x => x.Nombre));

    private static string Unir(string principal, List<string> avisos) =>
        avisos.Count == 0 ? principal + "." : principal + ". " + string.Join(". ", avisos) + ".";

    public Task<Resultado> ActualizarCamionAsync(CamionRequest req, string usuario, string? ip, CancellationToken ct = default)
        => EnTransaccionAsync(usuario, ip, async (cn, tx) =>
    {
        var cam = await LeerCamionAsync(cn, tx, req.Numero, ct);
        if (cam is null) return Resultado.Mal("Truck not found");

        var plantaNueva = Vacio(req.Planta);
        var estadoNuevo = string.IsNullOrWhiteSpace(req.Estado) ? cam.Estado : req.Estado!.Trim().ToLowerInvariant();
        if (estadoNuevo is not ("manned" or "down" or "open")) return Resultado.Mal("Invalid status");

        var conductores = await ConductoresDeAsync(cn, tx, cam.Id, ct);
        var nuevoConductor = req.IdConductor is > 0 ? req.IdConductor : null;

        if (plantaNueva == cam.Planta && estadoNuevo == cam.Estado && nuevoConductor is null)
            return Resultado.Bien("No changes");

        // Regla 2: Manned exige al menos un conductor
        if (estadoNuevo == "manned" && conductores.Count == 0 && nuevoConductor is null)
            return Resultado.Mal($"Truck #{cam.Numero}: a Manned truck must have at least one driver. Select the driver to assign.");
        // Regla 1: solo los Manned llevan conductores
        if (nuevoConductor is not null && estadoNuevo != "manned")
            return Resultado.Mal(MensajeSoloManned(cam.Numero, estadoNuevo));

        var avisos = new List<string>();

        // Regla 3: Open / Down liberan a sus conductores
        if (estadoNuevo != "manned" && conductores.Count > 0)
        {
            await using (var cmd = new NpgsqlCommand("UPDATE mezcladoras.conductor SET id_camion = NULL WHERE id_camion = @id", cn, tx))
            {
                cmd.Parameters.AddWithValue("id", cam.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            foreach (var d in conductores)
                await HistorialAsync(cn, tx, "CONDUCTOR", d.Nombre, "Truck", "#" + cam.Numero, "No truck", usuario, ip, ct);
            avisos.Add($"{Nombres(conductores)} {(conductores.Count == 1 ? "was" : "were")} released from the truck");
            conductores.Clear();
        }

        await using (var cmd = new NpgsqlCommand(@"
            UPDATE mezcladoras.camion SET
                id_planta = (SELECT id_planta FROM mezcladoras.planta WHERE codigo = @p),
                id_estado = (SELECT id_estado FROM mezcladoras.estado_camion WHERE codigo = @e)
            WHERE id_camion = @id", cn, tx))
        {
            cmd.Parameters.AddWithValue("p", (object?)plantaNueva ?? DBNull.Value);
            cmd.Parameters.AddWithValue("e", estadoNuevo);
            cmd.Parameters.AddWithValue("id", cam.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (plantaNueva != cam.Planta)
            await HistorialAsync(cn, tx, "CAMION", "#" + cam.Numero, "Plant", cam.Planta ?? "Unassigned", plantaNueva ?? "Unassigned", usuario, ip, ct);
        if (estadoNuevo != cam.Estado)
            await HistorialAsync(cn, tx, "CAMION", "#" + cam.Numero, "Status", cam.Estado, estadoNuevo, usuario, ip, ct);

        // Regla 4: los conductores del camión se mueven con él a la nueva planta
        if (plantaNueva != cam.Planta && conductores.Count > 0)
        {
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE mezcladoras.conductor SET id_planta = (SELECT id_planta FROM mezcladoras.planta WHERE codigo = @p)
                 WHERE id_camion = @id AND activo", cn, tx))
            {
                cmd.Parameters.AddWithValue("p", (object?)plantaNueva ?? DBNull.Value);
                cmd.Parameters.AddWithValue("id", cam.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            foreach (var d in conductores)
                await HistorialAsync(cn, tx, "CONDUCTOR", d.Nombre, "Plant", cam.Planta ?? "Unassigned", plantaNueva ?? "Unassigned", usuario, ip, ct);
        }

        // Conductor elegido al pasar a Manned (o agregado a un Manned)
        if (nuevoConductor is long idCond)
        {
            string nombre; string? plantaAnt; long? idCamAnt; int? numCamAnt;
            await using (var cmd = new NpgsqlCommand(@"
                SELECT d.nombre, p.codigo, d.id_camion, c.numero
                FROM mezcladoras.conductor d
                LEFT JOIN mezcladoras.planta p ON p.id_planta = d.id_planta
                LEFT JOIN mezcladoras.camion c ON c.id_camion = d.id_camion
                WHERE d.id_conductor = @id AND d.activo FOR UPDATE OF d", cn, tx))
            {
                cmd.Parameters.AddWithValue("id", idCond);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Resultado.Mal("Driver not found");
                nombre = rd.GetString(0);
                plantaAnt = rd.IsDBNull(1) ? null : rd.GetString(1);
                idCamAnt = rd.IsDBNull(2) ? null : rd.GetInt64(2);
                numCamAnt = rd.IsDBNull(3) ? null : rd.GetInt32(3);
            }

            if (idCamAnt != cam.Id)
            {
                if (conductores.Count >= MaxConductoresPorCamion) return Resultado.Mal(MensajeCamionLleno(cam.Numero));
                await using (var cmd = new NpgsqlCommand(@"
                    UPDATE mezcladoras.conductor
                       SET id_camion = @c, id_planta = (SELECT id_planta FROM mezcladoras.planta WHERE codigo = @p)
                     WHERE id_conductor = @id", cn, tx))
                {
                    cmd.Parameters.AddWithValue("c", cam.Id);
                    cmd.Parameters.AddWithValue("p", (object?)plantaNueva ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("id", idCond);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await HistorialAsync(cn, tx, "CONDUCTOR", nombre, "Truck", numCamAnt.HasValue ? "#" + numCamAnt : "No truck", "#" + cam.Numero, usuario, ip, ct);
                if (plantaAnt != plantaNueva)
                    await HistorialAsync(cn, tx, "CONDUCTOR", nombre, "Plant", plantaAnt ?? "Unassigned", plantaNueva ?? "Unassigned", usuario, ip, ct);
                avisos.Add($"{nombre} assigned");
                if (idCamAnt is long anterior && await AbrirSiQuedaSinConductoresAsync(cn, tx, anterior, usuario, ip, ct) is int abierto)
                    avisos.Add($"Truck #{abierto} changed to Open Trucks (no drivers left)");
            }
        }

        return Resultado.Bien(Unir($"Truck #{cam.Numero} updated", avisos));
    }, ct);

    public Task<Resultado> EliminarCamionAsync(int numero, string usuario, string? ip, CancellationToken ct = default)
        => EnTransaccionAsync(usuario, ip, async (cn, tx) =>
    {
        var cam = await LeerCamionAsync(cn, tx, numero, ct);
        if (cam is null) return Resultado.Mal("Truck not found");

        var liberados = await ConductoresDeAsync(cn, tx, cam.Id, ct);
        await using (var cmd = new NpgsqlCommand("UPDATE mezcladoras.conductor SET id_camion = NULL WHERE id_camion = @id", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", cam.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("DELETE FROM mezcladoras.camion WHERE id_camion = @id", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", cam.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var d in liberados)
            await HistorialAsync(cn, tx, "CONDUCTOR", d.Nombre, "Truck", "#" + numero, "No truck", usuario, ip, ct);
        await HistorialAsync(cn, tx, "CAMION", "#" + numero, "Baja", "#" + numero, null, usuario, ip, ct);
        return Resultado.Bien($"Truck #{numero} removed");
    }, ct);

    // ------------------------------------------------------------------
    // Conductores
    // ------------------------------------------------------------------
    public Task<Resultado> AgregarConductorAsync(ConductorRequest req, string usuario, string? ip, CancellationToken ct = default)
        => EnTransaccionAsync(usuario, ip, async (cn, tx) =>
    {
        var nombre = (req.Nombre ?? "").Trim();
        if (nombre.Length == 0) return Resultado.Mal("Enter the driver name");
        if (nombre.Length > 150) nombre = nombre[..150];

        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM mezcladoras.conductor WHERE lower(nombre) = lower(@n)", cn, tx))
        {
            cmd.Parameters.AddWithValue("n", nombre);
            if (await cmd.ExecuteScalarAsync(ct) != null) return Resultado.Mal("This driver is already on the list");
        }

        // Con camión: Manned u Open (el Open pasa a Manned); el conductor hereda su planta
        long? idCamion = null;
        var planta = Vacio(req.Planta);
        string? avisoCamion = null;
        if (req.NumeroCamion.HasValue)
        {
            var cam = await LeerCamionAsync(cn, tx, req.NumeroCamion.Value, ct);
            if (cam is null) return Resultado.Mal($"Truck #{req.NumeroCamion} does not exist");
            var (listo, aviso) = await PrepararCamionParaConductorAsync(cn, tx, cam, usuario, ip, ct);
            if (!listo) return Resultado.Mal(aviso!);
            avisoCamion = aviso;
            idCamion = cam.Id;
            planta = cam.Planta;
        }

        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO mezcladoras.conductor (nombre, id_camion, id_planta)
            VALUES (@n, @idc, (SELECT id_planta FROM mezcladoras.planta WHERE codigo = @p))", cn, tx))
        {
            cmd.Parameters.AddWithValue("n", nombre);
            cmd.Parameters.AddWithValue("idc", (object?)idCamion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("p", (object?)planta ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await HistorialAsync(cn, tx, "CONDUCTOR", nombre, "Alta", null,
            req.NumeroCamion.HasValue ? "#" + req.NumeroCamion : (planta ?? "Unassigned"), usuario, ip, ct);
        return Resultado.Bien(Unir("Driver added", avisoCamion is null ? new() : new() { avisoCamion }));
    }, ct);

    public Task<Resultado> ActualizarConductorAsync(ConductorRequest req, string usuario, string? ip, CancellationToken ct = default)
        => EnTransaccionAsync(usuario, ip, async (cn, tx) =>
    {
        string nombre; string? plantaActual; long? idCamionActual; int? camionActual; string? plantaCamionActual;
        await using (var cmd = new NpgsqlCommand(@"
            SELECT d.nombre, p.codigo, d.id_camion, c.numero, pc.codigo
            FROM mezcladoras.conductor d
            LEFT JOIN mezcladoras.planta p  ON p.id_planta  = d.id_planta
            LEFT JOIN mezcladoras.camion c  ON c.id_camion  = d.id_camion
            LEFT JOIN mezcladoras.planta pc ON pc.id_planta = c.id_planta
            WHERE d.id_conductor = @id FOR UPDATE OF d", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", req.IdConductor);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Resultado.Mal("Driver not found");
            nombre = rd.GetString(0);
            plantaActual = rd.IsDBNull(1) ? null : rd.GetString(1);
            idCamionActual = rd.IsDBNull(2) ? null : rd.GetInt64(2);
            camionActual = rd.IsDBNull(3) ? null : rd.GetInt32(3);
            plantaCamionActual = rd.IsDBNull(4) ? null : rd.GetString(4);
        }

        var camionNuevo = req.NumeroCamion;
        long? idCamionNuevo = idCamionActual;
        // Planta null en la petición = sin cambio ("Unassigned" = sin planta)
        var plantaNueva = req.Planta is null ? plantaActual : Vacio(req.Planta);
        var dejaCamionPorPlanta = false;
        var avisos = new List<string>();

        if (camionNuevo != camionActual)
        {
            if (camionNuevo.HasValue)
            {
                var cam = await LeerCamionAsync(cn, tx, camionNuevo.Value, ct);
                if (cam is null) return Resultado.Mal($"Truck #{camionNuevo} does not exist");
                var (listo, aviso) = await PrepararCamionParaConductorAsync(cn, tx, cam, usuario, ip, ct);
                if (!listo) return Resultado.Mal(aviso!);
                if (aviso != null) avisos.Add(aviso);
                idCamionNuevo = cam.Id;
                plantaNueva = cam.Planta;            // hereda la planta del camión
            }
            else idCamionNuevo = null;
        }
        else if (camionActual.HasValue && plantaNueva != plantaActual && plantaNueva != plantaCamionActual)
        {
            // Regla 4: se va a otra planta → deja el camión (el camión sigue en la suya)
            camionNuevo = null;
            idCamionNuevo = null;
            dejaCamionPorPlanta = true;
        }

        if (camionNuevo == camionActual && plantaNueva == plantaActual) return Resultado.Bien("No changes");

        await using (var cmd = new NpgsqlCommand(@"
            UPDATE mezcladoras.conductor SET
                id_camion = @idc,
                id_planta = (SELECT id_planta FROM mezcladoras.planta WHERE codigo = @p)
            WHERE id_conductor = @id", cn, tx))
        {
            cmd.Parameters.AddWithValue("idc", (object?)idCamionNuevo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("p", (object?)plantaNueva ?? DBNull.Value);
            cmd.Parameters.AddWithValue("id", req.IdConductor);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (camionNuevo != camionActual)
            await HistorialAsync(cn, tx, "CONDUCTOR", nombre, "Truck",
                camionActual.HasValue ? "#" + camionActual : "No truck",
                camionNuevo.HasValue ? "#" + camionNuevo : "No truck", usuario, ip, ct);
        if (plantaNueva != plantaActual)
            await HistorialAsync(cn, tx, "CONDUCTOR", nombre, "Plant", plantaActual ?? "Unassigned", plantaNueva ?? "Unassigned", usuario, ip, ct);

        if (idCamionActual is long anterior && anterior != idCamionNuevo
            && await AbrirSiQuedaSinConductoresAsync(cn, tx, anterior, usuario, ip, ct) is int abierto)
            avisos.Add($"Truck #{abierto} changed to Open Trucks (no drivers left)");

        string principal;
        if (dejaCamionPorPlanta) principal = $"{nombre} moved to {plantaNueva ?? "Unassigned"} and removed from truck #{camionActual}";
        else if (camionNuevo != camionActual && camionNuevo.HasValue) principal = $"{nombre} assigned to truck #{camionNuevo}";
        else if (camionNuevo != camionActual) principal = $"{nombre} removed from truck #{camionActual}";
        else principal = $"{nombre} moved to {plantaNueva ?? "Unassigned"}";
        return Resultado.Bien(Unir(principal, avisos));
    }, ct);

    public Task<Resultado> EliminarConductorAsync(long idConductor, string usuario, string? ip, CancellationToken ct = default)
        => EnTransaccionAsync(usuario, ip, async (cn, tx) =>
    {
        string nombre; long? idCamion;
        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM mezcladoras.conductor WHERE id_conductor = @id RETURNING nombre, id_camion", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", idConductor);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Resultado.Mal("Driver not found");
            nombre = rd.GetString(0);
            idCamion = rd.IsDBNull(1) ? null : rd.GetInt64(1);
        }
        await HistorialAsync(cn, tx, "CONDUCTOR", nombre, "Baja", nombre, null, usuario, ip, ct);

        var avisos = new List<string>();
        if (idCamion is long id && await AbrirSiQuedaSinConductoresAsync(cn, tx, id, usuario, ip, ct) is int abierto)
            avisos.Add($"Truck #{abierto} changed to Open Trucks (no drivers left)");
        return Resultado.Bien(Unir($"{nombre} removed", avisos));
    }, ct);

    // ------------------------------------------------------------------
    // Asignación conductor ↔ camión
    // ------------------------------------------------------------------
    public async Task<Resultado> AsignarConductorAsync(AsignacionRequest req, string usuario, string? ip, CancellationToken ct = default)
        // Planta = null → sin cambio; la planta la hereda del camión
        => await ActualizarConductorAsync(
            new ConductorRequest { IdConductor = req.IdConductor, NumeroCamion = req.Numero }, usuario, ip, ct);

    public Task<Resultado> QuitarConductorDeCamionAsync(long idConductor, string usuario, string? ip, CancellationToken ct = default)
        => EnTransaccionAsync(usuario, ip, async (cn, tx) =>
    {
        string nombre; long? idCamion; int? numero;
        await using (var cmd = new NpgsqlCommand(@"
            SELECT d.nombre, d.id_camion, c.numero FROM mezcladoras.conductor d
            LEFT JOIN mezcladoras.camion c ON c.id_camion = d.id_camion
            WHERE d.id_conductor = @id FOR UPDATE OF d", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", idConductor);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Resultado.Mal("Driver not found");
            nombre = rd.GetString(0);
            idCamion = rd.IsDBNull(1) ? null : rd.GetInt64(1);
            numero = rd.IsDBNull(2) ? null : rd.GetInt32(2);
        }
        if (idCamion is null) return Resultado.Bien("No changes");

        await using (var cmd = new NpgsqlCommand("UPDATE mezcladoras.conductor SET id_camion = NULL WHERE id_conductor = @id", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", idConductor);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await HistorialAsync(cn, tx, "CONDUCTOR", nombre, "Truck", "#" + numero, "No truck", usuario, ip, ct);

        var avisos = new List<string>();
        if (await AbrirSiQuedaSinConductoresAsync(cn, tx, idCamion.Value, usuario, ip, ct) is int abierto)
            avisos.Add($"Truck #{abierto} changed to Open Trucks (no drivers left)");
        return Resultado.Bien(Unir($"{nombre} removed from truck #{numero}", avisos));
    }, ct);

    // ------------------------------------------------------------------
    // Informe por correo
    // ------------------------------------------------------------------
    public async Task<ConfigReporte> ObtenerConfigReporteAsync(CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        var cfg = new ConfigReporte();

        await using (var cmd = new NpgsqlCommand(@"
            SELECT activo, to_char(hora_envio,'HH24:MI'), dias_semana, zona_horaria, asunto,
                   incluir_plantas, incluir_camiones, ultimo_envio
            FROM mezcladoras.reporte_config WHERE id_config = 1", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            if (await rd.ReadAsync(ct))
            {
                cfg.Activo = rd.GetBoolean(0);
                cfg.HoraEnvio = rd.GetString(1);
                cfg.DiasSemana = rd.GetString(2);
                cfg.ZonaHoraria = rd.GetString(3);
                cfg.Asunto = rd.GetString(4);
                cfg.IncluirPlantas = rd.GetBoolean(5);
                cfg.IncluirCamiones = rd.GetBoolean(6);
                cfg.UltimoEnvio = rd.IsDBNull(7) ? null : rd.GetDateTime(7);
            }
        }

        await using (var cmd = new NpgsqlCommand(@"
            SELECT id_destinatario, correo, nombre, tipo, activo
            FROM mezcladoras.reporte_destinatario ORDER BY tipo, lower(correo)", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
                cfg.Destinatarios.Add(new DestinatarioReporte
                {
                    IdDestinatario = rd.GetInt64(0),
                    Correo = rd.GetString(1),
                    Nombre = rd.IsDBNull(2) ? null : rd.GetString(2),
                    Tipo = rd.GetString(3),
                    Activo = rd.GetBoolean(4)
                });
        }
        return cfg;
    }

    public async Task<Resultado> GuardarConfigReporteAsync(ConfigReporte config, string usuario, string? ip, CancellationToken ct = default)
    {
        if (!TimeSpan.TryParse(config.HoraEnvio, out _)) return Resultado.Mal("Invalid time (use HH:mm)");

        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ContextoAsync(cn, tx, usuario, ip, ct);

        await using (var cmd = new NpgsqlCommand(@"
            UPDATE mezcladoras.reporte_config SET
                activo = @a, hora_envio = @h::time, dias_semana = @d, zona_horaria = @z,
                asunto = @s, incluir_plantas = @ip, incluir_camiones = @ic
            WHERE id_config = 1", cn, tx))
        {
            cmd.Parameters.AddWithValue("a", config.Activo);
            cmd.Parameters.AddWithValue("h", config.HoraEnvio);
            cmd.Parameters.AddWithValue("d", string.IsNullOrWhiteSpace(config.DiasSemana) ? "1,2,3,4,5" : config.DiasSemana);
            cmd.Parameters.AddWithValue("z", string.IsNullOrWhiteSpace(config.ZonaHoraria) ? "America/Chicago" : config.ZonaHoraria);
            cmd.Parameters.AddWithValue("s", string.IsNullOrWhiteSpace(config.Asunto) ? "NBR Ready Mix — Daily Fleet Status" : config.Asunto);
            cmd.Parameters.AddWithValue("ip", config.IncluirPlantas);
            cmd.Parameters.AddWithValue("ic", config.IncluirCamiones);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Los destinatarios llegan completos: se reemplaza la lista
        var correos = config.Destinatarios
            .Where(d => !string.IsNullOrWhiteSpace(d.Correo) && d.Correo.Contains('@'))
            .GroupBy(d => d.Correo.Trim().ToLowerInvariant()).Select(g => g.First()).ToList();

        await using (var cmd = new NpgsqlCommand("DELETE FROM mezcladoras.reporte_destinatario WHERE lower(correo) <> ALL(@c)", cn, tx))
        {
            cmd.Parameters.AddWithValue("c", correos.Select(d => d.Correo.Trim().ToLowerInvariant()).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var d in correos)
        {
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO mezcladoras.reporte_destinatario (correo, nombre, tipo, activo)
                VALUES (@c, @n, @t, @a)
                ON CONFLICT (lower(correo)) DO UPDATE SET nombre = EXCLUDED.nombre, tipo = EXCLUDED.tipo, activo = EXCLUDED.activo", cn, tx);
            cmd.Parameters.AddWithValue("c", d.Correo.Trim());
            cmd.Parameters.AddWithValue("n", (object?)d.Nombre ?? DBNull.Value);
            cmd.Parameters.AddWithValue("t", d.Tipo is "TO" or "CC" or "BCC" ? d.Tipo : "TO");
            cmd.Parameters.AddWithValue("a", d.Activo);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return Resultado.Bien("Report settings saved");
    }

    public async Task<List<EnvioReporte>> ObtenerEnviosAsync(int limite = 20, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        var lista = new List<EnvioReporte>();
        await using var cmd = new NpgsqlCommand(@"
            SELECT fecha, disparo, destinatarios, exito, mensaje_error, usuario
            FROM mezcladoras.reporte_envio ORDER BY fecha DESC LIMIT @l", cn);
        cmd.Parameters.AddWithValue("l", limite);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lista.Add(new EnvioReporte
            {
                Fecha = rd.GetDateTime(0),
                Disparo = rd.GetString(1),
                Destinatarios = rd.GetString(2),
                Exito = rd.GetBoolean(3),
                MensajeError = rd.IsDBNull(4) ? null : rd.GetString(4),
                Usuario = rd.GetString(5)
            });
        return lista;
    }

    public async Task RegistrarEnvioAsync(string disparo, string destinatarios, string asunto, bool exito, string? error,
                                          ResumenFlota resumen, string usuario, string? ip, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO mezcladoras.reporte_envio
                (disparo, destinatarios, asunto, exito, mensaje_error, total_camiones, cantidad_manned,
                 cantidad_down, cantidad_open, total_conductores, usuario, ip)
            VALUES (@d, @dest, @a, @e, @err, @t, @m, @dn, @o, @c, @u, NULLIF(@ip,'')::inet)", cn);
        cmd.Parameters.AddWithValue("d", disparo);
        cmd.Parameters.AddWithValue("dest", destinatarios);
        cmd.Parameters.AddWithValue("a", asunto);
        cmd.Parameters.AddWithValue("e", exito);
        cmd.Parameters.AddWithValue("err", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("t", resumen.TotalCamiones);
        cmd.Parameters.AddWithValue("m", resumen.Manned);
        cmd.Parameters.AddWithValue("dn", resumen.Down);
        cmd.Parameters.AddWithValue("o", resumen.OpenTrucks);
        cmd.Parameters.AddWithValue("c", resumen.TotalConductores);
        cmd.Parameters.AddWithValue("u", usuario);
        cmd.Parameters.AddWithValue("ip", ip ?? "");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IntentarReclamarEnvioAsync(DateTime ocurrenciaUtc, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE mezcladoras.reporte_config
               SET ultimo_envio = @o
             WHERE id_config = 1 AND (ultimo_envio IS NULL OR ultimo_envio < @o)
         RETURNING 1", cn);
        cmd.Parameters.AddWithValue("o", ocurrenciaUtc);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }
}
