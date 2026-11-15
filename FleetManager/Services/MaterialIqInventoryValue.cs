using System.Globalization;
using System.Text.Json.Nodes;

namespace FleetManager.Services;

public record MqConfirmedInventory(string Plant, string Sku, decimal Qty, decimal? Value, decimal? ProductValue,
    decimal? FreightValue, decimal? UnallocatedValue, decimal? RoundingAdjustment, string BalanceDate, string? ClosingId,
    decimal? AverageCost=null, decimal? ProductUnitCost=null, decimal? FreightUnitCost=null, decimal? UnallocatedUnitCost=null);

public static class MaterialIqInventoryValue
{
    static string S(JsonObject d, string key) => d[key]?.ToString() ?? "";
    static decimal? N(JsonObject d, string key) => d[key] is JsonValue v && v.TryGetValue<decimal>(out var n) ? n : null;
    static decimal Money(decimal n) => Math.Round(n, 2, MidpointRounding.AwayFromZero);
    public static JsonObject? FindInitial(JsonObject seed, IReadOnlyList<MqModelEvent> events, string plant, string sku, string cutoff)
    {
        var initial=((JsonArray)seed["inventories"]!).OfType<JsonObject>().FirstOrDefault(d=>S(d,"plant")==plant && S(d,"sku")==sku && string.CompareOrdinal(S(d,"date"),cutoff)<=0);
        if(initial!=null) return initial;
        var assignment=events.Where(e=>string.CompareOrdinal(S(e.Data,"date"),cutoff)<=0 &&
            ((e.Type=="catalogAssign" && S(e.Data,"plant")==plant && S(e.Data,"sku")==sku) ||
             (e.Type=="catalogPlant" && S(e.Data,"plant")==plant && e.Data["skus"] is JsonArray skus && skus.Any(s=>s?.ToString()==sku)) ||
             (e.Type=="catalogSku" && S(e.Data,"sku")==sku && e.Data["plants"] is JsonArray plants && plants.Any(p=>p?.ToString()==plant))))
            .OrderBy(e=>S(e.Data,"date"),StringComparer.Ordinal).FirstOrDefault();
        return assignment==null ? null : new JsonObject {["plant"]=plant,["sku"]=sku,["qty"]=0,["value"]=0,["date"]=S(assignment.Data,"date")};
    }
    public static bool ValidClosing(JsonObject d) =>
        DateOnly.TryParseExact(S(d,"date"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _) &&
        N(d,"qty") is >= 0 && N(d,"value") is >= 0 &&
        ((d["productValue"] == null && d["freightValue"] == null) ||
         (N(d,"productValue") is >= 0 && N(d,"freightValue") is >= 0 &&
          Math.Abs(N(d,"productValue")!.Value + N(d,"freightValue")!.Value - N(d,"value")!.Value) <= 0.01m));
    public static bool ValidCheckpoint(string type,JsonObject d) => type=="snapshot" ? ValidClosing(d) :
        type=="inventoryAdjustment" && DateOnly.TryParseExact(S(d,"date"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _) &&
        N(d,"qty") is >=0 && N(d,"value") is >=0 && N(d,"productValue") is >=0 && N(d,"freightValue") is >=0 && N(d,"unallocatedValue") is >=0 &&
        N(d,"averageCost") is >=0 && Math.Abs((N(d,"productValue")+N(d,"freightValue")+N(d,"unallocatedValue")+(N(d,"roundingAdjustment") ?? 0)-N(d,"value"))!.Value)<=0.01m;

    public static MqConfirmedInventory Replay(JsonObject initial, IReadOnlyList<MqModelEvent> source, string cutoff)
    {
        var plant=S(initial,"plant"); var sku=S(initial,"sku");
        var events=source.OrderBy(e=>e.Created).ThenBy(e=>e.Id).ToArray();
        var closing=events.Where(e=>(e.Type is "snapshot" or "inventoryAdjustment") && S(e.Data,"plant")==plant && S(e.Data,"sku")==sku &&
            string.CompareOrdinal(S(e.Data,"date"),cutoff)<=0 && ValidCheckpoint(e.Type,e.Data))
            .OrderBy(e=>S(e.Data,"date"),StringComparer.Ordinal).ThenBy(e=>e.Created).ThenBy(e=>e.Id).LastOrDefault();
        var d=closing?.Data ?? initial;
        decimal qty=N(d,"qty") ?? 0;
        var start=S(d,"date");
        decimal? value=N(d,"value") is >=0 ? Money(N(d,"value")!.Value) : null;
        var split=closing?.Type=="inventoryAdjustment" || ValidClosing(d) && d["productValue"]!=null;
        decimal? product=value==null ? null : split ? Money(N(d,"productValue")!.Value) : 0;
        decimal? freight=value==null ? null : split ? Money(N(d,"freightValue")!.Value) : 0;
        decimal? unallocated=value==null ? null : split ? 0 : value;
        decimal? average=closing?.Type=="inventoryAdjustment" ? N(d,"averageCost") : value!=null && qty>0 ? value/qty : null;
        decimal? productUnit=closing?.Type=="inventoryAdjustment" ? N(d,"productUnitCost") : qty>0 && value!=null ? product/qty : null;
        decimal? freightUnit=closing?.Type=="inventoryAdjustment" ? N(d,"freightUnitCost") : qty>0 && value!=null ? freight/qty : null;
        decimal? unallocatedUnit=closing?.Type=="inventoryAdjustment" ? N(d,"unallocatedUnitCost") : qty>0 && value!=null ? unallocated/qty : null;
        if(closing?.Type=="inventoryAdjustment") unallocated=N(d,"unallocatedValue") ?? unallocated;
        if(string.CompareOrdinal(start,cutoff)>0) return new(plant,sku,0,null,null,null,null,null,start,closing?.Id.ToString());
        var moves=new List<(string Date,int Kind,DateTime Created,Guid Id,decimal Qty,decimal Product,decimal Freight)>();
        foreach(var order in events.Where(e=>e.Type=="purchase"))
        {
            var purchase=(JsonObject)order.Data.DeepClone(); var voided=false;
            foreach(var change in events.Where(e=>S(e.Data,"order")==order.Id.ToString() && string.CompareOrdinal(S(e.Data,"date"),cutoff)<=0)) {
                if(change.Type=="purchaseEdit" && change.Data["replacement"] is JsonObject replacement)
                    foreach(var pair in replacement) purchase[pair.Key]=pair.Value?.DeepClone();
                if(change.Type=="purchaseVoid") voided=change.Data["voided"]?.GetValue<bool>()==true;
            }
            if(voided || S(purchase,"plant")!=plant || S(purchase,"sku")!=sku || string.CompareOrdinal(S(purchase,"date"),cutoff)>0) continue;
            var receipt=events.FirstOrDefault(e=>e.Type=="receipt" && S(e.Data,"order")==order.Id.ToString() && string.CompareOrdinal(S(e.Data,"date"),cutoff)<=0);
            var day=S(purchase,"status")=="received" ? S(purchase,"date") : receipt==null ? "" : S(receipt.Data,"date");
            if(day.Length==0 || string.CompareOrdinal(day,start)<=0 || string.CompareOrdinal(day,cutoff)>0) continue;
            moves.Add((day,0,receipt?.Created ?? order.Created,order.Id,N(purchase,"qty") ?? 0,N(purchase,"product") ?? 0,N(purchase,"freight") ?? 0));
        }
        foreach(var usage in events.Where(e=>e.Type=="usage" && S(e.Data,"plant")==plant && S(e.Data,"sku")==sku &&
            string.CompareOrdinal(S(e.Data,"date"),start)>0 && string.CompareOrdinal(S(e.Data,"date"),cutoff)<=0))
            moves.Add((S(usage.Data,"date"),1,usage.Created,usage.Id,N(usage.Data,"qty") ?? 0,0,0));
        // The reference Site stores legacy recounts as quantityAdjustment, not inventoryAdjustment.
        foreach(var adjustment in events.Where(e=>e.Type=="quantityAdjustment" && S(e.Data,"plant")==plant && S(e.Data,"sku")==sku &&
            N(e.Data,"qty") is >=0 && string.CompareOrdinal(S(e.Data,"date"),cutoff)<=0 &&
            (string.CompareOrdinal(S(e.Data,"date"),start)>0 || S(e.Data,"date")==start && (closing==null || e.Created>closing.Created))))
            moves.Add((S(adjustment.Data,"date"),2,adjustment.Created,adjustment.Id,N(adjustment.Data,"qty")!.Value,0,0));
        foreach(var move in moves.OrderBy(m=>m.Date,StringComparer.Ordinal).ThenBy(m=>m.Kind).ThenBy(m=>m.Created).ThenBy(m=>m.Id))
        {
            if(move.Kind==0) {
                if(value==null && qty==0) {value=product=freight=unallocated=0;}
                if(value!=null) {value+=Money(move.Qty*(move.Product+move.Freight)); product+=Money(move.Qty*move.Product); freight+=Money(move.Qty*move.Freight);}
                qty+=move.Qty;
            } else if(move.Kind==2) {
                if(value!=null && average!=null) {
                    value=Money(move.Qty*average.Value);product=Money(move.Qty*productUnit!.Value);
                    freight=Money(move.Qty*freightUnit!.Value);unallocated=Money(move.Qty*unallocatedUnit!.Value);
                } else if(move.Qty==0 && value!=null) {value=product=freight=unallocated=0;}
                else {value=product=freight=unallocated=null;}
                qty=move.Qty;
            } else {
                var remaining=qty-move.Qty;
                if(remaining<0) {value=product=freight=unallocated=null;}
                else if(remaining==0) {value=product=freight=unallocated=0;}
                else if(value!=null && qty>0) {var ratio=remaining/qty;value=Money(value.Value*ratio);product=Money(product!.Value*ratio);freight=Money(freight!.Value*ratio);unallocated=Money(unallocated!.Value*ratio);}
                qty=remaining;
            }
            if(value==null || qty<0) {average=productUnit=freightUnit=unallocatedUnit=null;}
            else if(qty>0 && move.Kind!=2) {average=value/qty;productUnit=product/qty;freightUnit=freight/qty;unallocatedUnit=unallocated/qty;}
        }
        return new(plant,sku,qty,value,product,freight,unallocated,value-product-freight-unallocated,start,closing?.Id.ToString(),average,productUnit,freightUnit,unallocatedUnit);
    }

    public static JsonObject AdjustTons(MqConfirmedInventory before, decimal counted)
    {
        if(counted<0) throw new ArgumentException("Physical tons cannot be negative.");
        if(counted==before.Qty) throw new ArgumentException("Enter physical tons different from the recorded balance.");
        if(before.Qty<0 || before.Value==null || before.AverageCost==null || before.ProductUnitCost==null || before.FreightUnitCost==null || before.UnallocatedUnitCost==null)
            throw new ArgumentException("A confirmed average cost is required. Use Update valued balance first.");
        var value=Money(counted*before.AverageCost.Value);
        var product=Money(counted*before.ProductUnitCost.Value);
        var freight=Money(counted*before.FreightUnitCost.Value);
        var unallocated=Money(counted*before.UnallocatedUnitCost.Value);
        return new JsonObject {["qty"]=counted,["value"]=value,["productValue"]=product,["freightValue"]=freight,
            ["unallocatedValue"]=unallocated,["roundingAdjustment"]=value-product-freight-unallocated,
            ["averageCost"]=before.AverageCost,["productUnitCost"]=before.ProductUnitCost,["freightUnitCost"]=before.FreightUnitCost,["unallocatedUnitCost"]=before.UnallocatedUnitCost};
    }
}
