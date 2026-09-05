using System.Data;
using FleetManager.Models;
using Npgsql;

namespace FleetManager.Services;

/// <summary>
/// Acceso a datos sobre PostgreSQL (esquema flota) usando Npgsql.
/// Cada escritura corre en una transacción con set_config('app.usuario'/'app.ip')
/// para que los triggers de auditoría registren usuario e IP.
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
    // Cadena de conexión: Railway expone DATABASE_URL (postgresql://user:pass@host:port/db)
    // ------------------------------------------------------------------
    public static string ConstruirCadenaConexion(IConfiguration config)
    {
        // Prioridad: variable DATABASE_URL (Railway) > ConnectionStrings:Flota (appsettings.json).
        // Ambas aceptan formato URL (postgresql://user:pass@host:port/db) o formato clave=valor.
        var valor = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(valor))
            valor = config.GetConnectionString("Flota");
        if (string.IsNullOrWhiteSpace(valor))
            throw new InvalidOperationException("Defina la variable DATABASE_URL o ConnectionStrings:Flota en appsettings.json.");

        valor = valor.Trim();
        if (!valor.StartsWith("postgres", StringComparison.OrdinalIgnoreCase) || !valor.Contains("://"))
            return valor; // ya viene en formato Host=...;Username=...

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

    private static async Task FijarContextoAuditoriaAsync(NpgsqlConnection cn, NpgsqlTransaction tx, string usuario, string? ip, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT set_config('app.usuario', @u, true), set_config('app.ip', @ip, true)", cn, tx);
        cmd.Parameters.AddWithValue("u", usuario);
        cmd.Parameters.AddWithValue("ip", (object?)ip ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------
    // Verificación de conexión al arrancar (no crea ni modifica nada)
    // ------------------------------------------------------------------
    public async Task VerificarConexionAsync(CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM flota.v_tablero_flota", cn);
        var n = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        _log.LogInformation("Conectado a PostgreSQL. Equipos en flota.v_tablero_flota: {n}", n);
    }

    // ------------------------------------------------------------------
    // Lectura
    // ------------------------------------------------------------------
    public async Task<DatosTablero> ObtenerDatosAsync(CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        var datos = new DatosTablero();

        await using (var cmd = new NpgsqlCommand(@"
            SELECT numero_equipo, grupo, es_camion, familia, numero_camion, numero_remolque,
                   ubicacion, estado, carga, actualizado_por, fecha_actualizacion
            FROM flota.v_tablero_flota
            ORDER BY grupo, numero_equipo", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                datos.Equipos.Add(new EquipoFila
                {
                    NumeroEquipo = rd.GetString(0),
                    Grupo = rd.GetString(1),
                    EsCamion = rd.GetBoolean(2),
                    Familia = rd.GetString(3),
                    NumeroCamion = rd.IsDBNull(4) ? null : rd.GetString(4),
                    NumeroRemolque = rd.IsDBNull(5) ? null : rd.GetString(5),
                    Ubicacion = rd.IsDBNull(6) ? null : rd.GetString(6),
                    Estado = rd.IsDBNull(7) ? null : rd.GetString(7),
                    Carga = rd.IsDBNull(8) ? null : rd.GetString(8),
                    ActualizadoPor = rd.GetString(9),
                    FechaActualizacion = rd.GetDateTime(10)
                });
            }
        }

        datos.Catalogos.Ubicaciones = await LeerCatalogoAsync(cn, "SELECT codigo, nombre, NULL FROM flota.ubicacion WHERE activo ORDER BY orden, codigo", ct);
        datos.Catalogos.Estados = await LeerCatalogoAsync(cn, "SELECT codigo, nombre, color_hex FROM flota.estado_equipo WHERE activo ORDER BY orden, codigo", ct);
        datos.Catalogos.Cargas = await LeerCatalogoAsync(cn, "SELECT codigo, nombre, color_hex FROM flota.estado_carga WHERE activo ORDER BY orden, codigo", ct);

        // Camiones disponibles para el dropdown de cada remolque, agrupados por familia (CT / T)
        await using (var cmd = new NpgsqlCommand(@"
            SELECT g.familia, e.numero_equipo
            FROM flota.equipo e JOIN flota.grupo_equipo g ON g.id_grupo = e.id_grupo
            WHERE g.es_camion AND e.activo
            ORDER BY g.familia, e.numero_equipo", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                var fam = rd.GetString(0);
                if (!datos.Catalogos.CamionesPorFamilia.TryGetValue(fam, out var lista))
                    datos.Catalogos.CamionesPorFamilia[fam] = lista = new List<string>();
                lista.Add(rd.GetString(1));
            }
        }

        await using (var cmd = new NpgsqlCommand(@"
            SELECT numero_equipo, campo, valor_anterior, valor_nuevo, usuario, fecha
            FROM flota.historial_cambio ORDER BY fecha DESC, id_historial DESC LIMIT 30", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                datos.Historial.Add(new HistorialItem
                {
                    NumeroEquipo = rd.GetString(0),
                    Campo = rd.GetString(1),
                    ValorAnterior = rd.IsDBNull(2) ? null : rd.GetString(2),
                    ValorNuevo = rd.IsDBNull(3) ? null : rd.GetString(3),
                    Usuario = rd.GetString(4),
                    Fecha = rd.GetDateTime(5)
                });
            }
        }

        await using (var cmd = new NpgsqlCommand(@"
            SELECT fecha, total, cantidad_ok, cantidad_down, disponibilidad, cantidad_cement, cantidad_ash, cantidad_empty
            FROM flota.snapshot_semanal ORDER BY fecha DESC, id_snapshot DESC LIMIT 52", cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
                datos.Snapshots.Add(LeerSnapshot(rd));
        }

        return datos;
    }

    private static SnapshotItem LeerSnapshot(NpgsqlDataReader rd) => new()
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

    private static async Task<List<OpcionCatalogo>> LeerCatalogoAsync(NpgsqlConnection cn, string sql, CancellationToken ct)
    {
        var lista = new List<OpcionCatalogo>();
        await using var cmd = new NpgsqlCommand(sql, cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lista.Add(new OpcionCatalogo
            {
                Codigo = rd.GetString(0),
                Nombre = rd.GetString(1),
                ColorHex = rd.IsDBNull(2) ? null : rd.GetString(2)
            });
        return lista;
    }

    // ------------------------------------------------------------------
    // Guardar una fila (botón Save). Replica la lógica de la página original:
    //  - Si el remolque toma un camión que ya jalaba otro remolque, ese otro queda sin camión.
    //  - Solo se registran en el historial los campos que cambiaron.
    // ------------------------------------------------------------------
    public async Task<ResultadoGuardar> GuardarFilaAsync(GuardarFilaRequest req, string usuario, string? ip, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await FijarContextoAuditoriaAsync(cn, tx, usuario, ip, ct);

        long idEquipo; bool esCamion; string familia;
        string? camionActual, ubicActual, estadoActual, cargaActual;

        await using (var cmd = new NpgsqlCommand(@"
            SELECT e.id_equipo, g.es_camion, g.familia, c.numero_equipo, u.codigo, s.codigo, l.codigo
            FROM flota.equipo e
            JOIN flota.grupo_equipo g ON g.id_grupo = e.id_grupo
            LEFT JOIN flota.equipo c ON c.id_equipo = e.id_camion
            LEFT JOIN flota.ubicacion u ON u.id_ubicacion = e.id_ubicacion
            LEFT JOIN flota.estado_equipo s ON s.id_estado = e.id_estado
            LEFT JOIN flota.estado_carga l ON l.id_carga = e.id_carga
            WHERE e.numero_equipo = @n AND e.activo
            FOR UPDATE OF e", cn, tx))
        {
            cmd.Parameters.AddWithValue("n", req.NumeroEquipo);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct))
                return new ResultadoGuardar { Ok = false, Mensaje = "Equipment not found" };
            idEquipo = rd.GetInt64(0);
            esCamion = rd.GetBoolean(1);
            familia = rd.GetString(2);
            camionActual = rd.IsDBNull(3) ? null : rd.GetString(3);
            ubicActual = rd.IsDBNull(4) ? null : rd.GetString(4);
            estadoActual = rd.IsDBNull(5) ? null : rd.GetString(5);
            cargaActual = rd.IsDBNull(6) ? null : rd.GetString(6);
        }

        string? camionNuevo = esCamion ? camionActual : Vacio(req.Camion);
        string? ubicNueva = Vacio(req.Ubicacion);
        string? estadoNuevo = Vacio(req.Estado);
        string? cargaNueva = Vacio(req.Carga);

        var cambios = new List<(string campo, string? viejo, string? nuevo)>();
        if (camionNuevo != camionActual) cambios.Add(("Truck", camionActual, camionNuevo));
        if (ubicNueva != ubicActual) cambios.Add(("Location", ubicActual, ubicNueva));
        if (estadoNuevo != estadoActual) cambios.Add(("Status", estadoActual, estadoNuevo));
        if (cargaNueva != cargaActual) cambios.Add(("Load", cargaActual, cargaNueva));

        if (cambios.Count == 0)
            return new ResultadoGuardar { Ok = true, Cambios = 0, Mensaje = "No changes" };

        long? idCamionNuevo = null;
        if (!esCamion && camionNuevo != null)
        {
            // Validar que el camión exista y sea de la misma familia (CT con CT, T con T)
            await using (var cmd = new NpgsqlCommand(@"
                SELECT e.id_equipo FROM flota.equipo e JOIN flota.grupo_equipo g ON g.id_grupo = e.id_grupo
                WHERE e.numero_equipo = @c AND g.es_camion AND g.familia = @f AND e.activo", cn, tx))
            {
                cmd.Parameters.AddWithValue("c", camionNuevo);
                cmd.Parameters.AddWithValue("f", familia);
                var r = await cmd.ExecuteScalarAsync(ct);
                if (r == null || r is DBNull)
                    return new ResultadoGuardar { Ok = false, Mensaje = $"Truck {camionNuevo} is not valid for this trailer" };
                idCamionNuevo = Convert.ToInt64(r);
            }

            // Si otro remolque ya tenía ese camión, se le quita (igual que la página original)
            await using (var cmd = new NpgsqlCommand(@"
                UPDATE flota.equipo
                   SET id_camion = NULL, actualizado_por = @u, fecha_actualizacion = CURRENT_TIMESTAMP
                 WHERE id_camion = @idc AND id_equipo <> @ide
             RETURNING id_equipo, numero_equipo", cn, tx))
            {
                cmd.Parameters.AddWithValue("u", usuario);
                cmd.Parameters.AddWithValue("idc", idCamionNuevo.Value);
                cmd.Parameters.AddWithValue("ide", idEquipo);
                long? otroId = null; string? otroNumero = null;
                await using (var rd = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await rd.ReadAsync(ct)) { otroId = rd.GetInt64(0); otroNumero = rd.GetString(1); }
                }
                if (otroId.HasValue)
                    await InsertarHistorialAsync(cn, tx, otroId.Value, otroNumero!, "Truck", camionNuevo, null, usuario, ip, ct);
            }
        }

        await using (var cmd = new NpgsqlCommand(@"
            UPDATE flota.equipo SET
                id_camion    = @idc,
                id_ubicacion = (SELECT id_ubicacion FROM flota.ubicacion WHERE codigo = @ub),
                id_estado    = (SELECT id_estado FROM flota.estado_equipo WHERE codigo = @es),
                id_carga     = (SELECT id_carga FROM flota.estado_carga WHERE codigo = @ca),
                actualizado_por = @u,
                fecha_actualizacion = CURRENT_TIMESTAMP
            WHERE id_equipo = @ide", cn, tx))
        {
            cmd.Parameters.AddWithValue("idc", (object?)idCamionNuevo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ub", (object?)ubicNueva ?? DBNull.Value);
            cmd.Parameters.AddWithValue("es", (object?)estadoNuevo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ca", (object?)cargaNueva ?? DBNull.Value);
            cmd.Parameters.AddWithValue("u", usuario);
            cmd.Parameters.AddWithValue("ide", idEquipo);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var (campo, viejo, nuevo) in cambios)
            await InsertarHistorialAsync(cn, tx, idEquipo, req.NumeroEquipo, campo, viejo, nuevo, usuario, ip, ct);

        await tx.CommitAsync(ct);
        return new ResultadoGuardar { Ok = true, Cambios = cambios.Count, Mensaje = "Saved — truck/trailer pairing updated" };
    }

    private static async Task InsertarHistorialAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long idEquipo, string numero,
        string campo, string? viejo, string? nuevo, string usuario, string? ip, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO flota.historial_cambio (id_equipo, numero_equipo, campo, valor_anterior, valor_nuevo, usuario, ip)
            VALUES (@ide, @n, @c, @v, @nv, @u, NULLIF(@ip, '')::inet)", cn, tx);
        cmd.Parameters.AddWithValue("ide", idEquipo);
        cmd.Parameters.AddWithValue("n", numero);
        cmd.Parameters.AddWithValue("c", campo);
        cmd.Parameters.AddWithValue("v", (object?)viejo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("nv", (object?)nuevo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("u", usuario);
        cmd.Parameters.AddWithValue("ip", ip ?? "");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------
    // Weekly Snapshot
    // ------------------------------------------------------------------
    public async Task<SnapshotItem> CrearSnapshotAsync(string usuario, string? ip, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await FijarContextoAuditoriaAsync(cn, tx, usuario, ip, ct);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO flota.snapshot_semanal
                (fecha, total, cantidad_ok, cantidad_down, disponibilidad, cantidad_cement, cantidad_ash, cantidad_empty)
            SELECT CURRENT_DATE,
                   count(*),
                   count(*) FILTER (WHERE estado = 'OK'),
                   count(*) FILTER (WHERE estado = 'DOWN'),
                   ROUND(CASE WHEN count(*) = 0 THEN 0 ELSE count(*) FILTER (WHERE estado = 'OK') * 100.0 / count(*) END, 1),
                   count(*) FILTER (WHERE carga = 'CEMENT'),
                   count(*) FILTER (WHERE carga = 'ASH'),
                   count(*) FILTER (WHERE carga = 'EMPTY')
            FROM flota.v_tablero_flota
            RETURNING fecha, total, cantidad_ok, cantidad_down, disponibilidad, cantidad_cement, cantidad_ash, cantidad_empty", cn, tx);

        SnapshotItem snap;
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            await rd.ReadAsync(ct);
            snap = LeerSnapshot(rd);
        }
        await tx.CommitAsync(ct);
        return snap;
    }

    private static string? Vacio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
