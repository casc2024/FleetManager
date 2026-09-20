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

    public async Task<Resultado> ActualizarCamionAsync(CamionRequest req, string usuario, string? ip, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await ContextoAsync(cn, tx, usuario, ip, ct);

        long idCamion; string? plantaActual, estadoActual;
        await using (var cmd = new NpgsqlCommand(@"
            SELECT c.id_camion, p.codigo, e.codigo
            FROM mezcladoras.camion c
            JOIN mezcladoras.estado_camion e ON e.id_estado = c.id_estado
            LEFT JOIN mezcladoras.planta p ON p.id_planta = c.id_planta
            WHERE c.numero = @n FOR UPDATE OF c", cn, tx))
        {
            cmd.Parameters.AddWithValue("n", req.Numero);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Resultado.Mal("Truck not found");
            idCamion = rd.GetInt64(0);
            plantaActual = rd.IsDBNull(1) ? null : rd.GetString(1);
            estadoActual = rd.GetString(2);
        }

        var plantaNueva = Vacio(req.Planta);
        var estadoNuevo = string.IsNullOrWhiteSpace(req.Estado) ? estadoActual : req.Estado!.Trim();

        var cambios = new List<string>();
        if (plantaNueva != plantaActual) cambios.Add("Plant");
        if (estadoNuevo != estadoActual) cambios.Add("Status");
        if (cambios.Count == 0) return Resultado.Bien("No changes");

        await using (var cmd = new NpgsqlCommand(@"
            UPDATE mezcladoras.camion SET
                id_planta = (SELECT id_planta FROM mezcladoras.planta WHERE codigo = @p),
                id_estado = (SELECT id_estado FROM mezcladoras.estado_camion WHERE codigo = @e)
            WHERE id_camion = @id", cn, tx))
        {
            cmd.Parameters.AddWithValue("p", (object?)plantaNueva ?? DBNull.Value);
            cmd.Parameters.AddWithValue("e", estadoNuevo!);
            cmd.Parameters.AddWithValue("id", idCamion);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Resultado.Mal("Truck not found");
        }

        // Al cambiar la planta del camión, sus conductores heredan esa planta
        var conductoresMovidos = new List<string>();
        if (plantaNueva != plantaActual)
        {
            await using var cmd = new NpgsqlCommand(@"
                UPDATE mezcladoras.conductor
                   SET id_planta = (SELECT id_planta FROM mezcladoras.planta WHERE codigo = @p)
                 WHERE id_camion = @id AND activo
             RETURNING nombre", cn, tx);
            cmd.Parameters.AddWithValue("p", (object?)plantaNueva ?? DBNull.Value);
            cmd.Parameters.AddWithValue("id", idCamion);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) conductoresMovidos.Add(rd.GetString(0));
        }

        if (plantaNueva != plantaActual)
            await HistorialAsync(cn, tx, "CAMION", "#" + req.Numero, "Plant", plantaActual ?? "Unassigned", plantaNueva ?? "Unassigned", usuario, ip, ct);
        if (estadoNuevo != estadoActual)
            await HistorialAsync(cn, tx, "CAMION", "#" + req.Numero, "Status", estadoActual, estadoNuevo, usuario, ip, ct);
        foreach (var nombre in conductoresMovidos)
            await HistorialAsync(cn, tx, "CONDUCTOR", nombre, "Plant", plantaActual ?? "Unassigned", plantaNueva ?? "Unassigned", usuario, ip, ct);

        // Si queda Down con conductores asignados, se avisa para reasignarlos
        var asignados = new List<string>();
        if (estadoNuevo == "down")
        {
            await using var cmd = new NpgsqlCommand("SELECT nombre FROM mezcladoras.conductor WHERE id_camion = @id AND activo ORDER BY nombre", cn, tx);
            cmd.Parameters.AddWithValue("id", idCamion);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) asignados.Add(rd.GetString(0));
        }

        await tx.CommitAsync(ct);

        var aviso = asignados.Count > 0
            ? $"Truck #{req.Numero} is Down. You need to reassign {(asignados.Count == 1 ? "the driver" : "the drivers")}: {string.Join(", ", asignados)} to another truck."
            : null;
        return Resultado.Bien("Truck updated in Overview and Plants", aviso);
    }

    public async Task<Resultado> EliminarCamionAsync(int numero, string usuario, string? ip, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ContextoAsync(cn, tx, usuario, ip, ct);

        long idCamion;
        await using (var cmd = new NpgsqlCommand("SELECT id_camion FROM mezcladoras.camion WHERE numero = @n", cn, tx))
        {
            cmd.Parameters.AddWithValue("n", numero);
            var r = await cmd.ExecuteScalarAsync(ct);
            if (r is null or DBNull) return Resultado.Mal("Truck not found");
            idCamion = Convert.ToInt64(r);
        }

        var liberados = new List<string>();
        await using (var cmd = new NpgsqlCommand("UPDATE mezcladoras.conductor SET id_camion = NULL WHERE id_camion = @id RETURNING nombre", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", idCamion);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) liberados.Add(rd.GetString(0));
        }

        await using (var cmd = new NpgsqlCommand("DELETE FROM mezcladoras.camion WHERE id_camion = @id", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", idCamion);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var nombre in liberados)
            await HistorialAsync(cn, tx, "CONDUCTOR", nombre, "Truck", "#" + numero, null, usuario, ip, ct);
        await HistorialAsync(cn, tx, "CAMION", "#" + numero, "Baja", "#" + numero, null, usuario, ip, ct);

        await tx.CommitAsync(ct);
        return Resultado.Bien($"Truck #{numero} removed");
    }

    // ------------------------------------------------------------------
    // Conductores
    // ------------------------------------------------------------------
    public async Task<Resultado> AgregarConductorAsync(ConductorRequest req, string usuario, string? ip, CancellationToken ct = default)
    {
        var nombre = (req.Nombre ?? "").Trim();
        if (nombre.Length == 0) return Resultado.Mal("Enter the driver name");
        if (nombre.Length > 150) nombre = nombre[..150];

        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ContextoAsync(cn, tx, usuario, ip, ct);

        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM mezcladoras.conductor WHERE lower(nombre) = lower(@n)", cn, tx))
        {
            cmd.Parameters.AddWithValue("n", nombre);
            if (await cmd.ExecuteScalarAsync(ct) != null) return Resultado.Mal("This driver is already on the list");
        }

        // Si se le asigna camión, hereda la planta del camión (y el trigger valida que no esté Down)
        long? idCamion = null;
        var planta = Vacio(req.Planta);
        if (req.NumeroCamion.HasValue)
        {
            await using var cmd = new NpgsqlCommand(@"
                SELECT c.id_camion, p.codigo FROM mezcladoras.camion c
                LEFT JOIN mezcladoras.planta p ON p.id_planta = c.id_planta
                WHERE c.numero = @num", cn, tx);
            cmd.Parameters.AddWithValue("num", req.NumeroCamion.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Resultado.Mal($"Truck #{req.NumeroCamion} does not exist");
            idCamion = rd.GetInt64(0);
            planta = rd.IsDBNull(1) ? null : rd.GetString(1);
        }

        try
        {
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO mezcladoras.conductor (nombre, id_camion, id_planta)
                VALUES (@n, @idc, (SELECT id_planta FROM mezcladoras.planta WHERE codigo = @p))", cn, tx);
            cmd.Parameters.AddWithValue("n", nombre);
            cmd.Parameters.AddWithValue("idc", (object?)idCamion ?? DBNull.Value);
            cmd.Parameters.AddWithValue("p", (object?)planta ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23514")
        {
            return Resultado.Mal($"Truck #{req.NumeroCamion} is Down. You must first change the truck status to Manned before assigning a driver.");
        }

        await HistorialAsync(cn, tx, "CONDUCTOR", nombre, "Alta", null,
            req.NumeroCamion.HasValue ? "#" + req.NumeroCamion : (Vacio(req.Planta) ?? "Unassigned"), usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Driver added");
    }

    public async Task<Resultado> ActualizarConductorAsync(ConductorRequest req, string usuario, string? ip, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await ContextoAsync(cn, tx, usuario, ip, ct);

        string nombre; string? plantaActual; int? camionActual;
        await using (var cmd = new NpgsqlCommand(@"
            SELECT d.nombre, p.codigo, c.numero
            FROM mezcladoras.conductor d
            LEFT JOIN mezcladoras.planta p ON p.id_planta = d.id_planta
            LEFT JOIN mezcladoras.camion c ON c.id_camion = d.id_camion
            WHERE d.id_conductor = @id FOR UPDATE OF d", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", req.IdConductor);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Resultado.Mal("Driver not found");
            nombre = rd.GetString(0);
            plantaActual = rd.IsDBNull(1) ? null : rd.GetString(1);
            camionActual = rd.IsDBNull(2) ? null : rd.GetInt32(2);
        }

        var camionNuevo = req.NumeroCamion;
        var plantaNueva = Vacio(req.Planta);

        // Con camión asignado, la planta del conductor la manda el camión
        if (camionNuevo.HasValue)
        {
            await using var cmd = new NpgsqlCommand(@"
                SELECT p.codigo FROM mezcladoras.camion c
                LEFT JOIN mezcladoras.planta p ON p.id_planta = c.id_planta
                WHERE c.numero = @n", cn, tx);
            cmd.Parameters.AddWithValue("n", camionNuevo.Value);
            var r = await cmd.ExecuteScalarAsync(ct);
            plantaNueva = r is null or DBNull ? null : (string)r;
        }

        if (camionNuevo == camionActual && plantaNueva == plantaActual) return Resultado.Bien("No changes");

        try
        {
            await using var cmd = new NpgsqlCommand(@"
                UPDATE mezcladoras.conductor SET
                    id_camion = (SELECT id_camion FROM mezcladoras.camion WHERE numero = @num),
                    id_planta = (SELECT id_planta FROM mezcladoras.planta WHERE codigo = @p)
                WHERE id_conductor = @id", cn, tx);
            cmd.Parameters.AddWithValue("num", (object?)camionNuevo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("p", (object?)plantaNueva ?? DBNull.Value);
            cmd.Parameters.AddWithValue("id", req.IdConductor);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23514")
        {
            return Resultado.Mal($"Truck #{camionNuevo} is Down. You must first change the truck status to Manned before assigning a driver.");
        }

        if (camionNuevo != camionActual)
            await HistorialAsync(cn, tx, "CONDUCTOR", nombre, "Truck",
                camionActual.HasValue ? "#" + camionActual : "No truck",
                camionNuevo.HasValue ? "#" + camionNuevo : "No truck", usuario, ip, ct);
        if (plantaNueva != plantaActual)
            await HistorialAsync(cn, tx, "CONDUCTOR", nombre, "Plant", plantaActual ?? "Unassigned", plantaNueva ?? "Unassigned", usuario, ip, ct);

        await tx.CommitAsync(ct);
        return Resultado.Bien("Changes saved");
    }

    public async Task<Resultado> EliminarConductorAsync(long idConductor, string usuario, string? ip, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ContextoAsync(cn, tx, usuario, ip, ct);

        string nombre;
        await using (var cmd = new NpgsqlCommand("DELETE FROM mezcladoras.conductor WHERE id_conductor = @id RETURNING nombre", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", idConductor);
            var r = await cmd.ExecuteScalarAsync(ct);
            if (r is null or DBNull) return Resultado.Mal("Driver not found");
            nombre = (string)r;
        }
        await HistorialAsync(cn, tx, "CONDUCTOR", nombre, "Baja", nombre, null, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Driver removed");
    }

    // ------------------------------------------------------------------
    // Asignación conductor ↔ camión
    // ------------------------------------------------------------------
    public async Task<Resultado> AsignarConductorAsync(AsignacionRequest req, string usuario, string? ip, CancellationToken ct = default)
    {
        var r = await ActualizarConductorAsync(
            new ConductorRequest { IdConductor = req.IdConductor, NumeroCamion = req.Numero }, usuario, ip, ct);
        return r.Ok ? Resultado.Bien("Driver added and plant synchronized") : r;
    }

    public async Task<Resultado> QuitarConductorDeCamionAsync(long idConductor, string usuario, string? ip, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ContextoAsync(cn, tx, usuario, ip, ct);

        string nombre; int? camionActual;
        await using (var cmd = new NpgsqlCommand(@"
            SELECT d.nombre, c.numero FROM mezcladoras.conductor d
            LEFT JOIN mezcladoras.camion c ON c.id_camion = d.id_camion
            WHERE d.id_conductor = @id", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", idConductor);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Resultado.Mal("Driver not found");
            nombre = rd.GetString(0);
            camionActual = rd.IsDBNull(1) ? null : rd.GetInt32(1);
        }

        await using (var cmd = new NpgsqlCommand("UPDATE mezcladoras.conductor SET id_camion = NULL WHERE id_conductor = @id", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", idConductor);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await HistorialAsync(cn, tx, "CONDUCTOR", nombre, "Truck", camionActual.HasValue ? "#" + camionActual : "No truck", "No truck", usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Driver removed from truck");
    }

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
