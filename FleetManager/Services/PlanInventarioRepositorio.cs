using FleetManager.Models;
using Npgsql;

namespace FleetManager.Services;

public partial class MaterialIqRepositorioPostgres
{
    private static string? ValidarFechasCompra(MqCompraReq req)
    {
        if(req.IdPlant<=0||req.IdMaterial<=0||req.PurchaseDate==default) return "Select a plant, material and purchase date.";
        if(req.Status is not (null or "" or "In transit" or "Received")) return "Invalid purchase status.";
        if(req.ExpectedDate?.Date<req.PurchaseDate.Date||req.ReceivedDate?.Date<req.PurchaseDate.Date)
            return "Arrival and receipt dates cannot precede the purchase date.";
        if(req.Reason?.Length>300) return "Business reason is limited to 300 characters.";
        return null;
    }

    private static async Task ContextoInventarioAsync(NpgsqlConnection cn, NpgsqlTransaction tx,
        string usuario, string? ip, object req, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"SELECT set_config('app.usuario',@u,true),
            set_config('app.ip',@ip,true),set_config('app.reason',@r,true)",cn,tx);
        cmd.Parameters.AddWithValue("u",usuario);
        cmd.Parameters.AddWithValue("ip",ip??"");
        cmd.Parameters.AddWithValue("r",req.GetType().GetProperty("Reason")?.GetValue(req) as string??"");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<object> OrigenAsync(DateTime cutoff,long plant,long material,CancellationToken ct=default)
    {
        await using var cn=await AbrirAsync(ct);
        await using var cmd=new NpgsqlCommand(@"SELECT balance_date,created_by,source_file,source_sheet,source_cell
            FROM materialiq.closing_balance WHERE id_plant=@p AND id_material=@m AND balance_date<=@c
            ORDER BY balance_date DESC LIMIT 1",cn);
        cmd.Parameters.AddWithValue("p",plant);cmd.Parameters.AddWithValue("m",material);cmd.Parameters.AddWithValue("c",cutoff.Date);
        await using var rd=await cmd.ExecuteReaderAsync(ct);
        if(!await rd.ReadAsync(ct)) return new { description="No closing balance recorded. Stock is reconstructed from received purchases and actual consumption." };
        var author=rd.IsDBNull(1)?null:rd.GetString(1);
        return new { balanceDate=rd.GetDateTime(0).ToString("yyyy-MM-dd"),createdBy=author,
            sourceFile=rd.IsDBNull(2)?null:rd.GetString(2),sourceSheet=rd.IsDBNull(3)?null:rd.GetString(3),sourceCell=rd.IsDBNull(4)?null:rd.GetString(4),
            description=!rd.IsDBNull(2)?"Imported closing balance with recorded workbook provenance.":author=="import-2026raw"?
                "Legacy import from the 2026 Raw inventory data. Workbook name and cell were not stored by the original import; they cannot be verified from this database.":
                "Manually recorded closing balance. This value is not attributed to an Excel cell." };
    }

    // A rate is replaced only by a newer rate for the SAME supplier and SKU.
    private async Task<Dictionary<(long, long), List<MqTarifaPlan>>> TarifasVigentesAsync(
        NpgsqlConnection cn, DateTime cutoff, long? plant, CancellationToken ct)
    {
        var result = new Dictionary<(long, long), List<MqTarifaPlan>>();
        await using var cmd = new NpgsqlCommand(@"
            SELECT r.id_plant, r.id_material, r.id_supplier, s.name,
                   r.product_price_t, r.freight_t, r.approved
            FROM (SELECT DISTINCT ON (id_plant, id_material, id_supplier) *
                  FROM materialiq.supplier_rate WHERE effective_date <= @c
                  ORDER BY id_plant, id_material, id_supplier, effective_date DESC, id_rate DESC) r
            JOIN materialiq.supplier s ON s.id_supplier=r.id_supplier AND s.is_active
            WHERE (@p::bigint IS NULL OR r.id_plant=@p)", cn);
        cmd.Parameters.AddWithValue("c", cutoff.Date);
        cmd.Parameters.AddWithValue("p", (object?)plant ?? DBNull.Value);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var key = (rd.GetInt64(0), rd.GetInt64(1));
            if (!result.TryGetValue(key, out var rates)) result[key] = rates = new();
            rates.Add(new(rd.GetInt64(2), rd.GetString(3), rd.GetDecimal(4), rd.GetDecimal(5), rd.GetBoolean(6)));
        }
        return result;
    }

    public async Task<object> Simular7DiasAsync(DateTime corte, long? plantId, CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        var stock = await CalcularStockAsync(cn, corte, plantId, ct);
        var rates = await TarifasVigentesAsync(cn, corte, plantId, ct);
        var demand = new Dictionary<(long, long, int), decimal>();
        var inbound = new Dictionary<(long, long, int), decimal>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT id_plant,id_material,demand_date-@c::date,tons FROM materialiq.demand
            WHERE demand_date BETWEEN @c::date+1 AND @c::date+7
              AND (@p::bigint IS NULL OR id_plant=@p)", cn))
        {
            cmd.Parameters.AddWithValue("c", corte.Date); cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) demand[(rd.GetInt64(0),rd.GetInt64(1),rd.GetInt32(2))]=rd.GetDecimal(3);
        }
        await using (var cmd = new NpgsqlCommand(@"
            SELECT id_plant,id_material,expected_date-@c::date,SUM(tons) FROM materialiq.purchase
            WHERE status='In transit' AND purchase_date<=@c
              AND expected_date BETWEEN @c::date+1 AND @c::date+7
              AND (@p::bigint IS NULL OR id_plant=@p)
            GROUP BY id_plant,id_material,expected_date", cn))
        {
            cmd.Parameters.AddWithValue("c", corte.Date); cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) inbound[(rd.GetInt64(0),rd.GetInt64(1),rd.GetInt32(2))]=rd.GetDecimal(3);
        }
        var history = new Dictionary<(long,long,DayOfWeek),List<decimal>>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT id_plant,id_material,use_date,SUM(tons) FROM materialiq.consumption
            WHERE use_date BETWEEN @c::date-27 AND @c AND (@p::bigint IS NULL OR id_plant=@p)
            GROUP BY id_plant,id_material,use_date", cn))
        {
            cmd.Parameters.AddWithValue("c", corte.Date); cmd.Parameters.AddWithValue("p", (object?)plantId ?? DBNull.Value);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while(await rd.ReadAsync(ct))
            {
                var key=(rd.GetInt64(0),rd.GetInt64(1),rd.GetDateTime(2).DayOfWeek);
                if(!history.TryGetValue(key,out var values)) history[key]=values=new();
                values.Add(rd.GetDecimal(3));
            }
        }
        var rows = new List<MqPlanFila>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT pm.id_plant,pm.id_material,p.name,m.mat_number,m.name,pm.safety_stock_t,
                   pm.purchase_lot_t,pm.max_capacity_t,pm.lead_time_days,pm.assigned_supplier_id,ps.id_supplier
            FROM materialiq.plant_material pm
            JOIN materialiq.plant p ON p.id_plant=pm.id_plant AND p.is_active
            JOIN materialiq.material m ON m.id_material=pm.id_material AND m.is_active
            LEFT JOIN materialiq.plan_selection ps ON ps.id_plant=pm.id_plant AND ps.id_material=pm.id_material AND ps.cutoff=@c
            WHERE (@p::bigint IS NULL OR pm.id_plant=@p) ORDER BY p.name,m.mat_number",cn))
        {
            cmd.Parameters.AddWithValue("c",corte.Date); cmd.Parameters.AddWithValue("p",(object?)plantId??DBNull.Value);
            await using var rd=await cmd.ExecuteReaderAsync(ct);
            while(await rd.ReadAsync(ct))
            {
                long pl=rd.GetInt64(0), mat=rd.GetInt64(1);
                var options=rates.GetValueOrDefault((pl,mat))??new();
                options=options.OrderBy(r=>r.Delivered).ThenBy(r=>r.IdSupplier).ToList();
                long? selected=rd.IsDBNull(10)?null:rd.GetInt64(10);
                var rate=selected!=null?options.FirstOrDefault(r=>r.IdSupplier==selected):options.FirstOrDefault();
                long? assigned=rd.IsDBNull(9)?null:rd.GetInt64(9);
                var assignedRate=options.FirstOrDefault(r=>r.IdSupplier==assigned);
                var ds=Enumerable.Range(1,7).Select(t=>demand.TryGetValue((pl,mat,t),out var d)?(decimal?)d:null).ToArray();
                var ins=Enumerable.Range(1,7).Select(t=>inbound.GetValueOrDefault((pl,mat,t))).ToArray();
                var days=CalculoInventario.Plan(corte,stock.GetValueOrDefault((pl,mat)).Q,rd.GetDecimal(5),rd.GetDecimal(6),
                    rd.IsDBNull(7)?null:rd.GetDecimal(7),rd.GetInt32(8),ds,ins);
                decimal? weekly=days.All(d=>d.Buy!=null)?days.Sum(d=>d.Buy!.Value):null;
                var baseValues=Enumerable.Range(1,7).Select(t=>history.GetValueOrDefault((pl,mat,corte.AddDays(t).DayOfWeek))).ToList();
                var first=days.FirstOrDefault(d=>d.Buy>0);
                rows.Add(new MqPlanFila {
                    IdPlant=pl,IdMaterial=mat,Plant=rd.GetString(2),MatNumber=rd.GetString(3),Material=rd.GetString(4),
                    LotTons=rd.GetDecimal(6),Dias=days,Rates=options,SelectedSupplierId=selected,AssignedSupplierId=assigned,
                    Supplier=rate?.Supplier,ProductPriceT=rate?.ProductPriceT,FreightT=rate?.FreightT,Delivered=rate?.Delivered,
                    Approved=rate?.Approved??false,WeeklyBuyTn=weekly,WeeklyBuyUsd=weekly*rate?.Delivered,
                    Savings=weekly!=null&&rate!=null&&assignedRate!=null?Math.Max(0,(assignedRate.Delivered-rate.Delivered)*weekly.Value):null,
                    Demand7Tn=ds.Sum(d=>d??0),PendingDays=ds.Count(d=>d==null),
                    BaselineTn=baseValues.All(v=>v!=null&&v.Count>=2)?baseValues.Sum(v=>v!.Average()):null,
                    FirstDay=first?.Day,FirstDate=first?.Date,CapRisk=days.Any(d=>d.CapRisk),LeadRisk=days.Any(d=>d.LeadRisk),
                    Status=ds.Any(d=>d==null)?"Incomplete plan":rate==null?"Rate required":!rate.Approved?"Approval pending":"Planned"
                });
            }
        }
        return new { fecha=corte.ToString("yyyy-MM-dd"), ready=rows.All(r=>r.Dias[0].Demand!=null), filas=rows };
    }

    public async Task<Resultado> GuardarSeleccionAsync(MqSeleccionReq req,string usuario,string? ip,CancellationToken ct=default)
    {
        if(req.IdPlant<=0||req.IdMaterial<=0||req.Cutoff==default||string.IsNullOrWhiteSpace(req.Reason)||req.Reason.Length>300)
            return Resultado.Mal("Select a plant, material, planning date and reason (1–300 characters).");
        await using var cn=await AbrirAsync(ct);
        await using var tx=await cn.BeginTransactionAsync(ct);
        await using(var check=new NpgsqlCommand("SELECT 1 FROM materialiq.plant_material WHERE id_plant=@p AND id_material=@m FOR UPDATE",cn,tx))
        {
            check.Parameters.AddWithValue("p",req.IdPlant);check.Parameters.AddWithValue("m",req.IdMaterial);
            if(await check.ExecuteScalarAsync(ct)==null)return Resultado.Mal("Plant/material assignment not found.");
        }
        if(req.IdSupplier!=null)
        {
            await using var check=new NpgsqlCommand(@"SELECT 1 FROM materialiq.supplier_rate r
                JOIN materialiq.supplier s ON s.id_supplier=r.id_supplier AND s.is_active
                WHERE r.id_plant=@p AND r.id_material=@m AND r.id_supplier=@s AND r.effective_date<=@c LIMIT 1",cn,tx);
            check.Parameters.AddWithValue("p",req.IdPlant);check.Parameters.AddWithValue("m",req.IdMaterial);
            check.Parameters.AddWithValue("s",req.IdSupplier.Value);check.Parameters.AddWithValue("c",req.Cutoff.Date);
            if(await check.ExecuteScalarAsync(ct)==null)return Resultado.Mal("Supplier has no effective rate for this plant and material.");
        }
        string? old=null;
        await using(var cmd=new NpgsqlCommand("SELECT id_supplier::text FROM materialiq.plan_selection WHERE id_plant=@p AND id_material=@m AND cutoff=@c",cn,tx))
        {
            cmd.Parameters.AddWithValue("p",req.IdPlant);cmd.Parameters.AddWithValue("m",req.IdMaterial);cmd.Parameters.AddWithValue("c",req.Cutoff.Date);
            old=(await cmd.ExecuteScalarAsync(ct)) as string;
        }
        await using(var cmd=new NpgsqlCommand(@"INSERT INTO materialiq.plan_selection(id_plant,id_material,cutoff,id_supplier,reason,updated_by)
            VALUES(@p,@m,@c,@s,@r,@u) ON CONFLICT(id_plant,id_material,cutoff)
            DO UPDATE SET id_supplier=EXCLUDED.id_supplier,reason=EXCLUDED.reason,updated_by=EXCLUDED.updated_by,updated_at=CURRENT_TIMESTAMP",cn,tx))
        {
            cmd.Parameters.AddWithValue("p",req.IdPlant);cmd.Parameters.AddWithValue("m",req.IdMaterial);cmd.Parameters.AddWithValue("c",req.Cutoff.Date);
            cmd.Parameters.AddWithValue("s",(object?)req.IdSupplier??DBNull.Value);cmd.Parameters.AddWithValue("r",req.Reason.Trim());cmd.Parameters.AddWithValue("u",usuario);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await LogAsync(cn,tx,"plan_selection",null,"Update","supplier",old??"Automatic",req.IdSupplier?.ToString()??"Automatic",req.Reason,usuario,ip,ct);
        await tx.CommitAsync(ct);
        return Resultado.Bien("Supplier selection saved. No purchase has been recorded.");
    }
}
