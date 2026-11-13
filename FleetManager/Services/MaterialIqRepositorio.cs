using System.Text.RegularExpressions;
using FleetManager.Models;
using Npgsql;

namespace FleetManager.Services;

public interface IMaterialIqRepositorio
{
    Task VerificarConexionAsync(CancellationToken ct = default);
    // Reads
    Task<List<MqPlanta>> ListarPlantasAsync(CancellationToken ct = default);
    Task<object> ListarMaterialesAsync(CancellationToken ct = default);
    Task<object> ListarProveedoresAsync(CancellationToken ct = default);
    Task<List<MqFila>> ObtenerFilasAsync(DateTime corte, long? plantId, CancellationToken ct = default);
    Task<decimal> ComprasHastaCorteAsync(DateTime corte, long? plantId, CancellationToken ct = default);
    Task<object> Simular7DiasAsync(DateTime corte, long? plantId, CancellationToken ct = default);
    Task<object> ListarDemandaAsync(DateTime fecha, long? plantId, CancellationToken ct = default);
    Task<object> ListarComprasAsync(DateTime desde, DateTime hasta, long? plantId, CancellationToken ct = default);
    Task<object> ListarTarifasAsync(long? plantId, CancellationToken ct = default);
    Task<object> ListarPoliticasAsync(long? plantId, CancellationToken ct = default);
    Task<object> ChartsAsync(DateTime desde, DateTime hasta, long? plantId, string modo, CancellationToken ct = default);
    Task<object> ListarHistorialAsync(DateTime desde, DateTime hasta, string? entity, CancellationToken ct = default);
    // Writes (audited)
    Task<Resultado> GuardarDemandaAsync(MqDemandaReq req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> CrearCompraAsync(MqCompraReq req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> RecibirCompraAsync(MqAccionCompraReq req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> EditarCompraAsync(MqCompraReq req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> AnularCompraAsync(MqAccionCompraReq req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> RestaurarCompraAsync(MqAccionCompraReq req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> GuardarSaldoAsync(MqSaldoReq req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> RegistrarConsumoAsync(MqConsumoReq req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> GuardarPoliticaAsync(MqPoliticaReq req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> CrearProveedorAsync(MqProveedorReq req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> CrearTarifaAsync(MqTarifaReq req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> AprobarTarifaAsync(MqIdReq req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> CrearPlantaAsync(MqPlantaReq req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> CrearMaterialAsync(MqMaterialReq req, string usuario, string? ip, CancellationToken ct = default);
    Task<Resultado> AsignarMaterialAsync(MqAsignarReq req, string usuario, string? ip, CancellationToken ct = default);
}

/// <summary>
/// Npgsql access to the NEW "materialiq" schema only. Nothing here reads or
/// writes fleet / seguridad / mezcladoras. Every mutation is logged to
/// materialiq.change_log with the signed-in user and client IP.
/// </summary>
public class MaterialIqRepositorioPostgres : IMaterialIqRepositorio
{
    private readonly string _cadena;
    public MaterialIqRepositorioPostgres(IConfiguration config)
        => _cadena = FlotaRepositorioPostgres.ConstruirCadenaConexion(config);

    // Server-side type/format guards (reject strange characters).
    private static readonly Regex ReName = new(@"^[\p{L}0-9 .,'&()/#\-]{2,120}$", RegexOptions.Compiled);
    private static readonly Regex ReCode = new(@"^[A-Za-z0-9_\-]{2,40}$", RegexOptions.Compiled);
    private static readonly Regex ReMatNum = new(@"^[A-Za-z0-9\-]{1,10}$", RegexOptions.Compiled);
    private static readonly Regex ReTicket = new(@"^[\p{L}0-9 .,'&()/#\-]{1,80}$", RegexOptions.Compiled);
    private const decimal MaxMoney = 1_000_000m, MaxTons = 100_000_000m;
    private static bool Rango(decimal v, decimal min, decimal max) => v >= min && v <= max;

    private async Task<NpgsqlConnection> AbrirAsync(CancellationToken ct)
    {
        var cn = new NpgsqlConnection(_cadena);
        await cn.OpenAsync(ct);
        return cn;
    }

    public async Task VerificarConexionAsync(CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM materialiq.material", cn);
        await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task LogAsync(NpgsqlConnection cn, NpgsqlTransaction tx, string entity, long? entityId,
        string action, string? field, string? oldV, string? newV, string? reason, string usuario, string? ip, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO materialiq.change_log (entity, entity_id, action, field, old_value, new_value, reason, changed_by, changed_ip)
            VALUES (@e, @id, @a, @f, @o, @n, @r, @u, NULLIF(@ip,'')::inet)", cn, tx);
        cmd.Parameters.AddWithValue("e", entity);
        cmd.Parameters.AddWithValue("id", (object?)entityId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("f", (object?)field ?? DBNull.Value);
        cmd.Parameters.AddWithValue("o", (object?)oldV ?? DBNull.Value);
        cmd.Parameters.AddWithValue("n", (object?)newV ?? DBNull.Value);
        cmd.Parameters.AddWithValue("r", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("u", usuario);
        cmd.Parameters.AddWithValue("ip", ip ?? "");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ================================================================
    // Reads
    // ================================================================
    public async Task<List<MqPlanta>> ListarPlantasAsync(CancellationToken ct = default)
    {
        var lista = new List<MqPlanta>();
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id_plant, code, name FROM materialiq.plant WHERE is_active ORDER BY name", cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lista.Add(new MqPlanta { IdPlant = rd.GetInt64(0), Code = rd.GetString(1), Name = rd.GetString(2) });
        return lista;
    }

    public async Task<object> ListarMaterialesAsync(CancellationToken ct = default)
    {
        var lista = new List<object>();
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id_material, mat_number, name, category, unit, density FROM materialiq.material WHERE is_active ORDER BY mat_number", cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lista.Add(new { idMaterial = rd.GetInt64(0), matNumber = rd.GetString(1), name = rd.GetString(2),
                category = rd.GetString(3), unit = rd.GetString(4), density = rd.IsDBNull(5) ? (decimal?)null : rd.GetDecimal(5) });
        return lista;
    }

    public async Task<object> ListarProveedoresAsync(CancellationToken ct = default)
    {
        var lista = new List<object>();
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id_supplier, name FROM materialiq.supplier WHERE is_active ORDER BY name", cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lista.Add(new { idSupplier = rd.GetInt64(0), name = rd.GetString(1) });
        return lista;
    }

    // Weighted-average stock + value per plant/material at the cutoff.
    // Latest closing balance resets stock/value; received purchases and
    // consumption dated after it are replayed in date order (receipt first).
    private async Task<Dictionary<(long, long), (decimal Q, decimal? V)>> CalcularStockAsync(
        NpgsqlConnection cn, DateTime corte, long? plantId, CancellationToken ct)
    {
        var baseBal = new Dictionary<(long, long), (DateTime date, decimal q, decimal? v)>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT DISTINCT ON (id_plant, id_material) id_plant, id_material, balance_date, tons, value_usd
            FROM materialiq.closing_balance
            WHERE balance_date <= @c AND (@p::bigint IS NULL OR id_plant = @p)
            ORDER BY id_plant, id_material, balance_date DESC", cn))
        {
            cmd.Parameters.AddWithValue("c", corte.Date);
            cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                baseBal[(rd.GetInt64(0), rd.GetInt64(1))] =
                    (rd.GetDateTime(2), rd.GetDecimal(3), rd.IsDBNull(4) ? (decimal?)null : rd.GetDecimal(4));
        }

        // movements: (key -> ordered list of (date, isReceipt, tons, unitCost))
        var mov = new Dictionary<(long, long), List<(DateTime date, bool receipt, decimal tons, decimal unit)>>();
        void Add((long, long) k, DateTime d, bool r, decimal t, decimal u)
        {
            if (!mov.TryGetValue(k, out var l)) { l = new(); mov[k] = l; }
            l.Add((d, r, t, u));
        }
        await using (var cmd = new NpgsqlCommand(@"
            SELECT id_plant, id_material, received_date, tons, product_price_t + freight_t
            FROM materialiq.purchase
            WHERE status = 'Received' AND received_date IS NOT NULL AND received_date <= @c
              AND (@p::bigint IS NULL OR id_plant = @p)", cn))
        {
            cmd.Parameters.AddWithValue("c", corte.Date);
            cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                Add((rd.GetInt64(0), rd.GetInt64(1)), rd.GetDateTime(2), true, rd.GetDecimal(3), rd.GetDecimal(4));
        }
        await using (var cmd = new NpgsqlCommand(@"
            SELECT id_plant, id_material, use_date, tons FROM materialiq.consumption
            WHERE use_date <= @c AND (@p::bigint IS NULL OR id_plant = @p)", cn))
        {
            cmd.Parameters.AddWithValue("c", corte.Date);
            cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                Add((rd.GetInt64(0), rd.GetInt64(1)), rd.GetDateTime(2), false, rd.GetDecimal(3), 0m);
        }

        var result = new Dictionary<(long, long), (decimal, decimal?)>();
        var keys = new HashSet<(long, long)>(baseBal.Keys);
        foreach (var k in mov.Keys) keys.Add(k);
        foreach (var k in keys)
        {
            DateTime d0 = DateTime.MinValue; decimal q = 0; decimal? v = 0; bool unknown = false;
            if (baseBal.TryGetValue(k, out var b)) { d0 = b.date; q = b.q; v = b.v; unknown = (b.v == null && b.q > 0); }
            if (mov.TryGetValue(k, out var list))
            {
                // date asc; receipts before usage on the same day; only strictly after the base date
                foreach (var m in list.Where(x => x.date > d0)
                                       .OrderBy(x => x.date).ThenByDescending(x => x.receipt))
                {
                    if (m.receipt)
                    {
                        q += m.tons;
                        if (!unknown && v != null) v += m.tons * m.unit;
                    }
                    else if (q > 0)
                    {
                        var u = Math.Min(m.tons, q);
                        if (!unknown && v != null) v = v.Value * (1 - u / q);
                        q -= u;
                    }
                }
            }
            result[k] = (q, unknown ? (decimal?)null : v);
        }
        return result;
    }

    public async Task<List<MqFila>> ObtenerFilasAsync(DateTime corte, long? plantId, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        var stock = await CalcularStockAsync(cn, corte, plantId, ct);

        var inbound = new Dictionary<(long, long), decimal>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT id_plant, id_material, SUM(tons) FROM materialiq.purchase
            WHERE status = 'In transit' AND purchase_date <= @c AND (@p::bigint IS NULL OR id_plant = @p)
            GROUP BY id_plant, id_material", cn))
        {
            cmd.Parameters.AddWithValue("c", corte.Date);
            cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) inbound[(rd.GetInt64(0), rd.GetInt64(1))] = rd.GetDecimal(2);
        }

        var demanda = new Dictionary<(long, long), decimal>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT id_plant, id_material, tons FROM materialiq.demand
            WHERE demand_date = @c::date + 1 AND (@p::bigint IS NULL OR id_plant = @p)", cn))
        {
            cmd.Parameters.AddWithValue("c", corte.Date);
            cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) demanda[(rd.GetInt64(0), rd.GetInt64(1))] = rd.GetDecimal(2);
        }

        var rate = new Dictionary<(long, long), decimal>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT id_plant, id_material, MIN(product_price_t + freight_t) FROM materialiq.supplier_rate
            WHERE approved AND effective_date <= @c AND (@p::bigint IS NULL OR id_plant = @p)
            GROUP BY id_plant, id_material", cn))
        {
            cmd.Parameters.AddWithValue("c", corte.Date);
            cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rate[(rd.GetInt64(0), rd.GetInt64(1))] = rd.GetDecimal(2);
        }

        var filas = new List<MqFila>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT pl.id_plant, pl.name, m.id_material, m.mat_number, m.name, m.unit,
                   pm.safety_stock_t, pm.purchase_lot_t
            FROM materialiq.plant_material pm
            JOIN materialiq.plant pl ON pl.id_plant = pm.id_plant AND pl.is_active
            JOIN materialiq.material m ON m.id_material = pm.id_material AND m.is_active
            WHERE (@p::bigint IS NULL OR pm.id_plant = @p)
            ORDER BY pl.name, m.mat_number", cn))
        {
            cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var key = (rd.GetInt64(0), rd.GetInt64(2));
                stock.TryGetValue(key, out var sv);
                filas.Add(new MqFila
                {
                    IdPlant = key.Item1, Plant = rd.GetString(1),
                    IdMaterial = key.Item2, MatNumber = rd.GetString(3), Material = rd.GetString(4), Unit = rd.GetString(5),
                    StockTons = stock.ContainsKey(key) ? sv.Q : 0m,
                    ValueUsd = stock.ContainsKey(key) ? sv.V : null,
                    DemandTomorrow = demanda.TryGetValue(key, out var dv) ? dv : null,
                    SafetyTons = rd.IsDBNull(6) ? 0 : rd.GetDecimal(6),
                    LotTons = rd.IsDBNull(7) ? 25 : rd.GetDecimal(7),
                    InboundTons = inbound.TryGetValue(key, out var iv) ? iv : 0m,
                    BestRateT = rate.TryGetValue(key, out var rv) ? rv : null
                });
            }
        }
        return filas;
    }

    public async Task<decimal> ComprasHastaCorteAsync(DateTime corte, long? plantId, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT COALESCE(SUM(tons * (product_price_t + freight_t)), 0) FROM materialiq.purchase
            WHERE status <> 'Voided' AND purchase_date <= @c AND (@p::bigint IS NULL OR id_plant = @p)", cn);
        cmd.Parameters.AddWithValue("c", corte.Date);
        cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<object> Simular7DiasAsync(DateTime corte, long? plantId, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        var stock = await CalcularStockAsync(cn, corte, plantId, ct);

        // demand for days 1..7, keyed by (plant, material, dayOffset 1..7)
        var dem = new Dictionary<(long, long, int), decimal>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT id_plant, id_material, (demand_date - @c::date) AS off, tons
            FROM materialiq.demand
            WHERE demand_date BETWEEN @c::date + 1 AND @c::date + 7 AND (@p::bigint IS NULL OR id_plant = @p)", cn))
        {
            cmd.Parameters.AddWithValue("c", corte.Date);
            cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) dem[(rd.GetInt64(0), rd.GetInt64(1), rd.GetInt32(2))] = rd.GetDecimal(3);
        }
        // inbound by expected arrival day
        var inb = new Dictionary<(long, long, int), decimal>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT id_plant, id_material, (expected_date - @c::date) AS off, SUM(tons)
            FROM materialiq.purchase
            WHERE status = 'In transit' AND expected_date BETWEEN @c::date + 1 AND @c::date + 7
              AND (@p::bigint IS NULL OR id_plant = @p)
            GROUP BY id_plant, id_material, (expected_date - @c::date)", cn))
        {
            cmd.Parameters.AddWithValue("c", corte.Date);
            cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) inb[(rd.GetInt64(0), rd.GetInt64(1), rd.GetInt32(2))] = rd.GetDecimal(3);
        }

        var salida = new List<object>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT pl.id_plant, pl.name, m.id_material, m.mat_number, m.name,
                   pm.safety_stock_t, pm.purchase_lot_t, pm.max_capacity_t, tar.r
            FROM materialiq.plant_material pm
            JOIN materialiq.plant pl ON pl.id_plant = pm.id_plant AND pl.is_active
            JOIN materialiq.material m ON m.id_material = pm.id_material AND m.is_active
            LEFT JOIN (SELECT id_plant, id_material, MIN(product_price_t + freight_t) AS r
                       FROM materialiq.supplier_rate WHERE approved AND effective_date <= @c
                       GROUP BY id_plant, id_material) tar
                   ON tar.id_plant = pm.id_plant AND tar.id_material = pm.id_material
            WHERE (@p::bigint IS NULL OR pm.id_plant = @p)
            ORDER BY pl.name, m.mat_number", cn))
        {
            cmd.Parameters.AddWithValue("c", corte.Date);
            cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                long idp = rd.GetInt64(0), idm = rd.GetInt64(2);
                decimal s = rd.IsDBNull(5) ? 0 : rd.GetDecimal(5);
                decimal l = rd.IsDBNull(6) ? 25 : rd.GetDecimal(6);
                decimal? cap = rd.IsDBNull(7) ? null : rd.GetDecimal(7);
                decimal? r = rd.IsDBNull(8) ? null : rd.GetDecimal(8);
                stock.TryGetValue((idp, idm), out var sv);
                decimal bal = stock.ContainsKey((idp, idm)) ? sv.Q : 0m;
                decimal weeklyBuy = 0; int? firstDay = null; bool capRisk = false;
                var dias = new List<object>();
                for (int t = 1; t <= 7; t++)
                {
                    decimal? d = dem.TryGetValue((idp, idm, t), out var dvv) ? dvv : null;
                    decimal i = inb.TryGetValue((idp, idm, t), out var ivv) ? ivv : 0m;
                    decimal x = bal + i - (d ?? 0);
                    decimal need = Math.Max(0m, s - x);
                    decimal buy = (l > 0 && need > 0) ? Math.Ceiling(need / l) * l : 0m;
                    bal = x + buy;
                    weeklyBuy += buy;
                    if (buy > 0 && firstDay == null) firstDay = t;
                    if (cap != null && bal > cap.Value) capRisk = true;
                    dias.Add(new { day = t, demand = d, inbound = i, buy, closing = bal });
                }
                salida.Add(new
                {
                    idPlant = idp, plant = rd.GetString(1), idMaterial = idm,
                    matNumber = rd.GetString(3), material = rd.GetString(4),
                    weeklyBuyTn = weeklyBuy, weeklyBuyUsd = r != null ? weeklyBuy * r.Value : (decimal?)null,
                    firstDay, capRisk, dias
                });
            }
        }
        return new { fecha = corte.ToString("yyyy-MM-dd"), filas = salida };
    }

    public async Task<object> ListarDemandaAsync(DateTime fecha, long? plantId, CancellationToken ct = default)
    {
        var lista = new List<object>();
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT pm.id_plant, pl.name, pm.id_material, m.mat_number, m.name, m.unit, m.density, d.tons, d.production_cy, d.dosage_t_cy
            FROM materialiq.plant_material pm
            JOIN materialiq.plant pl ON pl.id_plant = pm.id_plant AND pl.is_active
            JOIN materialiq.material m ON m.id_material = pm.id_material AND m.is_active
            LEFT JOIN materialiq.demand d ON d.id_plant = pm.id_plant AND d.id_material = pm.id_material AND d.demand_date = @f
            WHERE (@p::bigint IS NULL OR pm.id_plant = @p)
            ORDER BY pl.name, m.mat_number", cn);
        cmd.Parameters.AddWithValue("f", fecha.Date);
        cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lista.Add(new { idPlant = rd.GetInt64(0), plant = rd.GetString(1), idMaterial = rd.GetInt64(2),
                matNumber = rd.GetString(3), material = rd.GetString(4), unit = rd.GetString(5),
                density = rd.IsDBNull(6) ? (decimal?)null : rd.GetDecimal(6),
                tons = rd.IsDBNull(7) ? (decimal?)null : rd.GetDecimal(7),
                produccionCy = rd.IsDBNull(8) ? (decimal?)null : rd.GetDecimal(8),
                dosisTCy = rd.IsDBNull(9) ? (decimal?)null : rd.GetDecimal(9) });
        return new { fecha = fecha.ToString("yyyy-MM-dd"), filas = lista };
    }

    public async Task<object> ListarComprasAsync(DateTime desde, DateTime hasta, long? plantId, CancellationToken ct = default)
    {
        var lista = new List<object>();
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT pu.id_purchase, pl.name, m.mat_number, m.name, s.name, pu.purchase_date, pu.tons,
                   pu.product_price_t, pu.freight_t, pu.expected_date, pu.received_date, pu.status, pu.ticket_ref,
                   pu.id_plant, pu.id_material, pu.id_supplier
            FROM materialiq.purchase pu
            JOIN materialiq.plant pl ON pl.id_plant = pu.id_plant
            JOIN materialiq.material m ON m.id_material = pu.id_material
            LEFT JOIN materialiq.supplier s ON s.id_supplier = pu.id_supplier
            WHERE pu.purchase_date BETWEEN @d AND @h AND (@p::bigint IS NULL OR pu.id_plant = @p)
            ORDER BY pu.purchase_date DESC, pu.id_purchase DESC", cn);
        cmd.Parameters.AddWithValue("d", desde.Date);
        cmd.Parameters.AddWithValue("h", hasta.Date);
        cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lista.Add(new {
                idPurchase = rd.GetInt64(0), plant = rd.GetString(1), matNumber = rd.GetString(2), material = rd.GetString(3),
                supplier = rd.IsDBNull(4) ? null : rd.GetString(4), purchaseDate = rd.GetDateTime(5).ToString("yyyy-MM-dd"),
                tons = rd.GetDecimal(6), productPriceT = rd.GetDecimal(7), freightT = rd.GetDecimal(8),
                expectedDate = rd.IsDBNull(9) ? null : rd.GetDateTime(9).ToString("yyyy-MM-dd"),
                receivedDate = rd.IsDBNull(10) ? null : rd.GetDateTime(10).ToString("yyyy-MM-dd"),
                status = rd.GetString(11), ticketRef = rd.IsDBNull(12) ? null : rd.GetString(12),
                total = rd.GetDecimal(6) * (rd.GetDecimal(7) + rd.GetDecimal(8)),
                idPlant = rd.GetInt64(13), idMaterial = rd.GetInt64(14),
                idSupplier = rd.IsDBNull(15) ? (long?)null : rd.GetInt64(15)
            });
        return lista;
    }

    public async Task<object> ListarTarifasAsync(long? plantId, CancellationToken ct = default)
    {
        var lista = new List<object>();
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT r.id_rate, pl.name, m.mat_number, m.name, s.name, r.effective_date, r.product_price_t, r.freight_t, r.origin, r.approved
            FROM materialiq.supplier_rate r
            JOIN materialiq.plant pl ON pl.id_plant = r.id_plant
            JOIN materialiq.material m ON m.id_material = r.id_material
            JOIN materialiq.supplier s ON s.id_supplier = r.id_supplier
            WHERE (@p::bigint IS NULL OR r.id_plant = @p)
            ORDER BY pl.name, m.mat_number, r.effective_date DESC", cn);
        cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lista.Add(new { idRate = rd.GetInt64(0), plant = rd.GetString(1), matNumber = rd.GetString(2), material = rd.GetString(3),
                supplier = rd.GetString(4), effectiveDate = rd.GetDateTime(5).ToString("yyyy-MM-dd"),
                productPriceT = rd.GetDecimal(6), freightT = rd.GetDecimal(7), delivered = rd.GetDecimal(6) + rd.GetDecimal(7),
                origin = rd.IsDBNull(8) ? null : rd.GetString(8), approved = rd.GetBoolean(9) });
        return lista;
    }

    public async Task<object> ListarPoliticasAsync(long? plantId, CancellationToken ct = default)
    {
        var lista = new List<object>();
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT pm.id_plant, pl.name, pm.id_material, m.mat_number, m.name,
                   pm.safety_stock_t, pm.max_capacity_t, pm.lead_time_days, pm.purchase_lot_t
            FROM materialiq.plant_material pm
            JOIN materialiq.plant pl ON pl.id_plant = pm.id_plant
            JOIN materialiq.material m ON m.id_material = pm.id_material
            WHERE (@p::bigint IS NULL OR pm.id_plant = @p)
            ORDER BY pl.name, m.mat_number", cn);
        cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lista.Add(new { idPlant = rd.GetInt64(0), plant = rd.GetString(1), idMaterial = rd.GetInt64(2),
                matNumber = rd.GetString(3), material = rd.GetString(4),
                safetyStockT = rd.GetDecimal(5), maxCapacityT = rd.IsDBNull(6) ? (decimal?)null : rd.GetDecimal(6),
                leadTimeDays = rd.GetInt32(7), purchaseLotT = rd.GetDecimal(8) });
        return lista;
    }

    public async Task<object> ChartsAsync(DateTime desde, DateTime hasta, long? plantId, string modo, CancellationToken ct = default)
    {
        // "received": delivered tons by receipt date (status Received).
        // "recorded": ordered tons by purchase date, includes in-transit; excludes voided.
        bool recibido = string.Equals(modo, "received", StringComparison.OrdinalIgnoreCase);
        string filtro = recibido
            ? "pu.status = 'Received' AND pu.received_date BETWEEN @d AND @h"
            : "pu.status <> 'Voided' AND pu.purchase_date BETWEEN @d AND @h";

        var materiales = new List<object>();
        var plantas = new List<object>();
        await using var cn = await AbrirAsync(ct);

        await using (var cmd = new NpgsqlCommand($@"
            SELECT m.mat_number, m.name, SUM(pu.tons) AS t, SUM(pu.tons*(pu.product_price_t+pu.freight_t)) AS u
            FROM materialiq.purchase pu JOIN materialiq.material m ON m.id_material = pu.id_material
            WHERE {filtro} AND (@p::bigint IS NULL OR pu.id_plant = @p)
            GROUP BY m.mat_number, m.name ORDER BY t DESC", cn))
        {
            cmd.Parameters.AddWithValue("d", desde.Date); cmd.Parameters.AddWithValue("h", hasta.Date);
            cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                materiales.Add(new { matNumber = rd.GetString(0), name = rd.GetString(1), tons = rd.GetDecimal(2), usd = rd.GetDecimal(3) });
        }
        await using (var cmd = new NpgsqlCommand($@"
            SELECT pl.name, SUM(pu.tons) AS t, SUM(pu.tons*(pu.product_price_t+pu.freight_t)) AS u
            FROM materialiq.purchase pu JOIN materialiq.plant pl ON pl.id_plant = pu.id_plant
            WHERE {filtro} AND (@p::bigint IS NULL OR pu.id_plant = @p)
            GROUP BY pl.name ORDER BY t DESC", cn))
        {
            cmd.Parameters.AddWithValue("d", desde.Date); cmd.Parameters.AddWithValue("h", hasta.Date);
            cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                plantas.Add(new { plant = rd.GetString(0), tons = rd.GetDecimal(1), usd = rd.GetDecimal(2) });
        }
        return new { modo = recibido ? "received" : "recorded", materiales, plantas };
    }

    public async Task<object> ListarHistorialAsync(DateTime desde, DateTime hasta, string? entity, CancellationToken ct = default)
    {
        var lista = new List<object>();
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT changed_at, entity, entity_id, action, field, old_value, new_value, reason, changed_by, host(changed_ip)
            FROM materialiq.change_log
            WHERE changed_at >= @d AND changed_at < @h + 1
              AND (@e = '' OR entity = @e)
            ORDER BY changed_at DESC, id DESC LIMIT 1000", cn);
        cmd.Parameters.AddWithValue("d", desde.Date);
        cmd.Parameters.AddWithValue("h", hasta.Date);
        cmd.Parameters.AddWithValue("e", entity ?? "");
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lista.Add(new {
                at = rd.GetDateTime(0).ToString("yyyy-MM-dd HH:mm"), entity = rd.GetString(1),
                entityId = rd.IsDBNull(2) ? (long?)null : rd.GetInt64(2), action = rd.GetString(3),
                field = rd.IsDBNull(4) ? null : rd.GetString(4), oldValue = rd.IsDBNull(5) ? null : rd.GetString(5),
                newValue = rd.IsDBNull(6) ? null : rd.GetString(6), reason = rd.IsDBNull(7) ? null : rd.GetString(7),
                by = rd.IsDBNull(8) ? null : rd.GetString(8), ip = rd.IsDBNull(9) ? null : rd.GetString(9) });
        return lista;
    }

    // ================================================================
    // Writes
    // ================================================================
    public async Task<Resultado> GuardarDemandaAsync(MqDemandaReq req, string usuario, string? ip, CancellationToken ct = default)
    {
        decimal? tons = req.Tons;
        if (tons == null && req.ProduccionCy != null && req.DosisTCy != null) tons = req.ProduccionCy.Value * req.DosisTCy.Value;
        if (req.ProduccionCy is < 0 || req.DosisTCy is < 0) return Resultado.Mal("Production and dosage cannot be negative.");
        if (tons == null) return Resultado.Mal("Enter tons, or production CY and dosage.");
        if (!Rango(tons.Value, 0, MaxTons)) return Resultado.Mal("Tons must be between 0 and 100,000,000.");

        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO materialiq.demand (id_plant, id_material, demand_date, tons, production_cy, dosage_t_cy, updated_by)
            VALUES (@pl, @m, @d, @t, @cy, @do, @u)
            ON CONFLICT (id_plant, id_material, demand_date)
            DO UPDATE SET tons = EXCLUDED.tons, production_cy = EXCLUDED.production_cy,
                          dosage_t_cy = EXCLUDED.dosage_t_cy, updated_by = EXCLUDED.updated_by, updated_at = CURRENT_TIMESTAMP", cn, tx))
        {
            cmd.Parameters.AddWithValue("pl", req.IdPlant);
            cmd.Parameters.AddWithValue("m", req.IdMaterial);
            cmd.Parameters.AddWithValue("d", req.Fecha.Date);
            cmd.Parameters.AddWithValue("t", tons.Value);
            cmd.Parameters.AddWithValue("cy", (object?)req.ProduccionCy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("do", (object?)req.DosisTCy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("u", usuario);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(cn, tx, "demand", null, "Update", "tons", null, tons.Value.ToString("0.##"), null, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Demand saved.");
    }

    public async Task<Resultado> CrearCompraAsync(MqCompraReq req, string usuario, string? ip, CancellationToken ct = default)
    {
        if (!Rango(req.Tons, 0.0001m, MaxTons)) return Resultado.Mal("Tons must be between 0 and 100,000,000.");
        if (!Rango(req.ProductPriceT, 0, MaxMoney) || !Rango(req.FreightT, 0, MaxMoney)) return Resultado.Mal("Prices must be between 0 and 1,000,000.");
        if (!string.IsNullOrWhiteSpace(req.TicketRef) && !ReTicket.IsMatch(req.TicketRef.Trim())) return Resultado.Mal("Ticket contains invalid characters.");
        var status = req.Status == "Received" ? "Received" : "In transit";
        var received = status == "Received" ? (object)(req.ReceivedDate?.Date ?? req.PurchaseDate.Date) : DBNull.Value;

        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        long id;
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO materialiq.purchase (id_plant, id_material, id_supplier, purchase_date, tons, product_price_t, freight_t,
                                             expected_date, received_date, status, ticket_ref, created_by)
            VALUES (@pl, @m, @s, @pd, @t, @pp, @fr, @ed, @rd, @st, @tk, @u) RETURNING id_purchase", cn, tx))
        {
            cmd.Parameters.AddWithValue("pl", req.IdPlant);
            cmd.Parameters.AddWithValue("m", req.IdMaterial);
            cmd.Parameters.AddWithValue("s", (object?)req.IdSupplier ?? DBNull.Value);
            cmd.Parameters.AddWithValue("pd", req.PurchaseDate.Date);
            cmd.Parameters.AddWithValue("t", req.Tons);
            cmd.Parameters.AddWithValue("pp", req.ProductPriceT);
            cmd.Parameters.AddWithValue("fr", req.FreightT);
            cmd.Parameters.AddWithValue("ed", (object?)req.ExpectedDate?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("rd", received);
            cmd.Parameters.AddWithValue("st", status);
            cmd.Parameters.AddWithValue("tk", (object?)req.TicketRef ?? DBNull.Value);
            cmd.Parameters.AddWithValue("u", usuario);
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        await LogAsync(cn, tx, "purchase", id, "Create", "status", null, status, null, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Purchase recorded.", new { idPurchase = id });
    }

    public async Task<Resultado> RecibirCompraAsync(MqAccionCompraReq req, string usuario, string? ip, CancellationToken ct = default)
    {
        if (req.IdPurchase <= 0) return Resultado.Mal("Purchase not specified.");
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(@"
            UPDATE materialiq.purchase SET status = 'Received',
                   received_date = COALESCE(@rd, received_date, purchase_date),
                   ticket_ref = COALESCE(@tk, ticket_ref), updated_by = @u, updated_at = CURRENT_TIMESTAMP
            WHERE id_purchase = @id AND status = 'In transit'", cn, tx))
        {
            cmd.Parameters.AddWithValue("rd", (object?)req.ReceivedDate?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("tk", (object?)req.TicketRef ?? DBNull.Value);
            cmd.Parameters.AddWithValue("u", usuario);
            cmd.Parameters.AddWithValue("id", req.IdPurchase);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            if (n == 0) return Resultado.Mal("Order not found or already received.");
        }
        await LogAsync(cn, tx, "purchase", req.IdPurchase, "Receive", "status", "In transit", "Received", req.Reason, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Order received.");
    }

    public async Task<Resultado> EditarCompraAsync(MqCompraReq req, string usuario, string? ip, CancellationToken ct = default)
    {
        if (req.IdPurchase is not > 0) return Resultado.Mal("Purchase not specified.");
        if (string.IsNullOrWhiteSpace(req.Reason)) return Resultado.Mal("Provide a reason for the edit.");
        if (!Rango(req.Tons, 0.0001m, MaxTons)) return Resultado.Mal("Tons must be between 0 and 100,000,000.");
        if (!Rango(req.ProductPriceT, 0, MaxMoney) || !Rango(req.FreightT, 0, MaxMoney)) return Resultado.Mal("Prices must be between 0 and 1,000,000.");
        if (!string.IsNullOrWhiteSpace(req.TicketRef) && !ReTicket.IsMatch(req.TicketRef.Trim())) return Resultado.Mal("Ticket contains invalid characters.");
        var status = req.Status == "Received" ? "Received" : "In transit";
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(@"
            UPDATE materialiq.purchase SET id_supplier=@s, purchase_date=@pd, tons=@t, product_price_t=@pp, freight_t=@fr,
                   expected_date=@ed, status=@st, received_date = CASE WHEN @st='Received' THEN COALESCE(@rd, received_date, purchase_date) ELSE received_date END,
                   ticket_ref=@tk, edit_reason=@re, updated_by=@u, updated_at=CURRENT_TIMESTAMP
            WHERE id_purchase=@id AND status <> 'Voided'", cn, tx))
        {
            cmd.Parameters.AddWithValue("s", (object?)req.IdSupplier ?? DBNull.Value);
            cmd.Parameters.AddWithValue("pd", req.PurchaseDate.Date);
            cmd.Parameters.AddWithValue("t", req.Tons);
            cmd.Parameters.AddWithValue("pp", req.ProductPriceT);
            cmd.Parameters.AddWithValue("fr", req.FreightT);
            cmd.Parameters.AddWithValue("ed", (object?)req.ExpectedDate?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("st", status);
            cmd.Parameters.AddWithValue("rd", (object?)req.ReceivedDate?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("tk", (object?)req.TicketRef ?? DBNull.Value);
            cmd.Parameters.AddWithValue("re", req.Reason!.Trim());
            cmd.Parameters.AddWithValue("u", usuario);
            cmd.Parameters.AddWithValue("id", req.IdPurchase!.Value);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            if (n == 0) return Resultado.Mal("Order not found or voided.");
        }
        await LogAsync(cn, tx, "purchase", req.IdPurchase, "Update", null, null, null, req.Reason, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Purchase updated.");
    }

    public Task<Resultado> AnularCompraAsync(MqAccionCompraReq req, string usuario, string? ip, CancellationToken ct = default)
        => CambiarEstadoAsync(req.IdPurchase, "Voided", "Void", req.Reason, usuario, ip, ct);

    public Task<Resultado> RestaurarCompraAsync(MqAccionCompraReq req, string usuario, string? ip, CancellationToken ct = default)
        => CambiarEstadoAsync(req.IdPurchase, null, "Restore", req.Reason, usuario, ip, ct);

    private async Task<Resultado> CambiarEstadoAsync(long id, string? nuevo, string accion, string? reason, string usuario, string? ip, CancellationToken ct)
    {
        if (id <= 0) return Resultado.Mal("Purchase not specified.");
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        // Restore: back to Received if it had a received_date, otherwise In transit.
        var sql = nuevo != null
            ? "UPDATE materialiq.purchase SET status=@st, updated_by=@u, updated_at=CURRENT_TIMESTAMP WHERE id_purchase=@id"
            : "UPDATE materialiq.purchase SET status = CASE WHEN received_date IS NOT NULL THEN 'Received' ELSE 'In transit' END, updated_by=@u, updated_at=CURRENT_TIMESTAMP WHERE id_purchase=@id AND status='Voided'";
        await using (var cmd = new NpgsqlCommand(sql, cn, tx))
        {
            if (nuevo != null) cmd.Parameters.AddWithValue("st", nuevo);
            cmd.Parameters.AddWithValue("u", usuario);
            cmd.Parameters.AddWithValue("id", id);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            if (n == 0) return Resultado.Mal("Order not found.");
        }
        await LogAsync(cn, tx, "purchase", id, accion, "status", null, nuevo, reason, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien(accion == "Void" ? "Order voided." : "Order restored.");
    }

    public async Task<Resultado> GuardarSaldoAsync(MqSaldoReq req, string usuario, string? ip, CancellationToken ct = default)
    {
        if (!Rango(req.Tons, 0, MaxTons)) return Resultado.Mal("Tons must be between 0 and 100,000,000.");
        if (req.ValueUsd != null && (req.ValueUsd < 0 || req.ValueUsd > 1_000_000_000m)) return Resultado.Mal("Value is out of range.");
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO materialiq.closing_balance (id_plant, id_material, balance_date, tons, value_usd, created_by)
            VALUES (@pl, @m, @d, @t, @v, @u)
            ON CONFLICT (id_plant, id_material, balance_date)
            DO UPDATE SET tons = EXCLUDED.tons, value_usd = EXCLUDED.value_usd, created_by = EXCLUDED.created_by", cn, tx))
        {
            cmd.Parameters.AddWithValue("pl", req.IdPlant);
            cmd.Parameters.AddWithValue("m", req.IdMaterial);
            cmd.Parameters.AddWithValue("d", req.BalanceDate.Date);
            cmd.Parameters.AddWithValue("t", req.Tons);
            cmd.Parameters.AddWithValue("v", (object?)req.ValueUsd ?? DBNull.Value);
            cmd.Parameters.AddWithValue("u", usuario);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(cn, tx, "closing_balance", null, "Update", "tons", null, req.Tons.ToString("0.##"), null, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Closing balance saved.");
    }

    public async Task<Resultado> RegistrarConsumoAsync(MqConsumoReq req, string usuario, string? ip, CancellationToken ct = default)
    {
        if (!Rango(req.Tons, 0.0001m, MaxTons)) return Resultado.Mal("Tons must be between 0 and 100,000,000.");
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        long id;
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO materialiq.consumption (id_plant, id_material, use_date, tons, created_by)
            VALUES (@pl, @m, @d, @t, @u) RETURNING id_consumption", cn, tx))
        {
            cmd.Parameters.AddWithValue("pl", req.IdPlant);
            cmd.Parameters.AddWithValue("m", req.IdMaterial);
            cmd.Parameters.AddWithValue("d", req.UseDate.Date);
            cmd.Parameters.AddWithValue("t", req.Tons);
            cmd.Parameters.AddWithValue("u", usuario);
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        await LogAsync(cn, tx, "consumption", id, "Create", "tons", null, req.Tons.ToString("0.##"), null, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Consumption recorded.");
    }

    public async Task<Resultado> GuardarPoliticaAsync(MqPoliticaReq req, string usuario, string? ip, CancellationToken ct = default)
    {
        if (req.SafetyStockT < 0 || req.PurchaseLotT <= 0 || req.LeadTimeDays < 0 || req.LeadTimeDays > 90
            || req.SafetyStockT > MaxTons || req.PurchaseLotT > MaxTons || req.MaxCapacityT is < 0 or > MaxTons)
            return Resultado.Mal("Check safety (0+), lot (>0), lead time (0-90) and capacity.");
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO materialiq.plant_material (id_plant, id_material, safety_stock_t, max_capacity_t, lead_time_days, purchase_lot_t, updated_by)
            VALUES (@pl, @m, @s, @cap, @ld, @lot, @u)
            ON CONFLICT (id_plant, id_material)
            DO UPDATE SET safety_stock_t=EXCLUDED.safety_stock_t, max_capacity_t=EXCLUDED.max_capacity_t,
                          lead_time_days=EXCLUDED.lead_time_days, purchase_lot_t=EXCLUDED.purchase_lot_t,
                          updated_by=EXCLUDED.updated_by, updated_at=CURRENT_TIMESTAMP", cn, tx))
        {
            cmd.Parameters.AddWithValue("pl", req.IdPlant);
            cmd.Parameters.AddWithValue("m", req.IdMaterial);
            cmd.Parameters.AddWithValue("s", req.SafetyStockT);
            cmd.Parameters.AddWithValue("cap", (object?)req.MaxCapacityT ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ld", req.LeadTimeDays);
            cmd.Parameters.AddWithValue("lot", req.PurchaseLotT);
            cmd.Parameters.AddWithValue("u", usuario);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(cn, tx, "plant_material", null, "Update", null, null, null, null, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Policy saved.");
    }

    public async Task<Resultado> CrearProveedorAsync(MqProveedorReq req, string usuario, string? ip, CancellationToken ct = default)
    {
        var name = (req.Name ?? "").Trim();
        if (!ReName.IsMatch(name)) return Resultado.Mal("Enter a valid supplier name (2-120 characters, no strange symbols).");
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        long id;
        try
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO materialiq.supplier (name, created_by) VALUES (@n, @u) RETURNING id_supplier", cn, tx);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("u", usuario);
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        catch (PostgresException ex) when (ex.SqlState == "23505") { return Resultado.Mal("A supplier with that name already exists."); }
        await LogAsync(cn, tx, "supplier", id, "Create", "name", null, name, null, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Supplier created.", new { idSupplier = id });
    }

    public async Task<Resultado> CrearTarifaAsync(MqTarifaReq req, string usuario, string? ip, CancellationToken ct = default)
    {
        if (!Rango(req.ProductPriceT, 0, MaxMoney) || !Rango(req.FreightT, 0, MaxMoney)) return Resultado.Mal("Prices must be between 0 and 1,000,000.");
        if (req.Origin != null && req.Origin.Trim().Length > 160) return Resultado.Mal("Origin is too long (max 160).");
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        long id;
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO materialiq.supplier_rate (id_plant, id_material, id_supplier, effective_date, product_price_t, freight_t, origin, approved, created_by)
            VALUES (@pl, @m, @s, @d, @pp, @fr, @o, @ap, @u) RETURNING id_rate", cn, tx))
        {
            cmd.Parameters.AddWithValue("pl", req.IdPlant);
            cmd.Parameters.AddWithValue("m", req.IdMaterial);
            cmd.Parameters.AddWithValue("s", req.IdSupplier);
            cmd.Parameters.AddWithValue("d", req.EffectiveDate.Date);
            cmd.Parameters.AddWithValue("pp", req.ProductPriceT);
            cmd.Parameters.AddWithValue("fr", req.FreightT);
            cmd.Parameters.AddWithValue("o", (object?)req.Origin ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ap", req.Approved);
            cmd.Parameters.AddWithValue("u", usuario);
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        await LogAsync(cn, tx, "supplier_rate", id, "Create", "delivered", null, (req.ProductPriceT + req.FreightT).ToString("0.##"), null, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Rate created.", new { idRate = id });
    }

    public async Task<Resultado> AprobarTarifaAsync(MqIdReq req, string usuario, string? ip, CancellationToken ct = default)
    {
        if (req.Id <= 0) return Resultado.Mal("Rate not specified.");
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("UPDATE materialiq.supplier_rate SET approved = TRUE WHERE id_rate = @id", cn, tx))
        {
            cmd.Parameters.AddWithValue("id", req.Id);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Resultado.Mal("Rate not found.");
        }
        await LogAsync(cn, tx, "supplier_rate", req.Id, "Update", "approved", "false", "true", null, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Rate approved.");
    }

    public async Task<Resultado> CrearPlantaAsync(MqPlantaReq req, string usuario, string? ip, CancellationToken ct = default)
    {
        var code = (req.Code ?? "").Trim().ToUpperInvariant();
        var name = (req.Name ?? "").Trim();
        if (!ReCode.IsMatch(code)) return Resultado.Mal("Plant code: 2-40 letters, digits, - or _.");
        if (!ReName.IsMatch(name) || name.Length > 80) return Resultado.Mal("Enter a valid plant name.");
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        long id;
        try
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO materialiq.plant (code, name, created_by) VALUES (@c, @n, @u) RETURNING id_plant", cn, tx);
            cmd.Parameters.AddWithValue("c", code);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("u", usuario);
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        catch (PostgresException ex) when (ex.SqlState == "23505") { return Resultado.Mal("A plant with that code already exists."); }
        await LogAsync(cn, tx, "plant", id, "Create", "code", null, code, null, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Plant created.", new { idPlant = id });
    }

    public async Task<Resultado> CrearMaterialAsync(MqMaterialReq req, string usuario, string? ip, CancellationToken ct = default)
    {
        var num = (req.MatNumber ?? "").Trim();
        var name = (req.Name ?? "").Trim();
        var cat = (req.Category ?? "").Trim();
        var unit = (req.Unit ?? "TN").Trim().ToUpperInvariant();
        if (!ReMatNum.IsMatch(num)) return Resultado.Mal("Material number: 1-10 letters, digits or -.");
        if (!ReName.IsMatch(name)) return Resultado.Mal("Material name contains invalid characters.");
        if (Array.IndexOf(new[] { "Produced", "Screened", "Recycled", "Received", "Purchased" }, cat) < 0) return Resultado.Mal("Invalid category.");
        if (unit != "TN" && unit != "CY") return Resultado.Mal("Unit must be TN or CY.");
        if (req.Density is < 0 or > 10) return Resultado.Mal("Density must be between 0 and 10.");
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        long id;
        try
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO materialiq.material (mat_number, name, category, unit, density, created_by) VALUES (@n, @nm, @c, @u, @d, @usr) RETURNING id_material", cn, tx);
            cmd.Parameters.AddWithValue("n", num);
            cmd.Parameters.AddWithValue("nm", name);
            cmd.Parameters.AddWithValue("c", cat);
            cmd.Parameters.AddWithValue("u", unit);
            cmd.Parameters.AddWithValue("d", (object?)req.Density ?? DBNull.Value);
            cmd.Parameters.AddWithValue("usr", usuario);
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        catch (PostgresException ex) when (ex.SqlState == "23505") { return Resultado.Mal("A material with that number already exists."); }
        await LogAsync(cn, tx, "material", id, "Create", "mat_number", null, num, null, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Material created.", new { idMaterial = id });
    }

    public async Task<Resultado> AsignarMaterialAsync(MqAsignarReq req, string usuario, string? ip, CancellationToken ct = default)
    {
        if (req.IdPlant <= 0 || req.IdMaterial <= 0) return Resultado.Mal("Plant and material are required.");
        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO materialiq.plant_material (id_plant, id_material, safety_stock_t, purchase_lot_t, lead_time_days, updated_by)
            VALUES (@pl, @m, 0, 25, 1, @u) ON CONFLICT (id_plant, id_material) DO NOTHING", cn, tx))
        {
            cmd.Parameters.AddWithValue("pl", req.IdPlant);
            cmd.Parameters.AddWithValue("m", req.IdMaterial);
            cmd.Parameters.AddWithValue("u", usuario);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(cn, tx, "plant_material", null, "Create", null, null, null, null, usuario, ip, ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Material assigned to plant.");
    }
}
