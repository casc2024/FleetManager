using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FleetManager.Services;

public record MqModelEvent(Guid Id, string Type, JsonObject Data, DateTime Created);
public record MqSuggestionRate(string Id, string Supplier, decimal Product, decimal Freight, bool Approved, string Date);
public record MqSuggestionOrder(Guid Id, string Arrival, decimal Qty);
public record MqSuggestionInput(string Cutoff, string Plant, string Sku, decimal? Inventory, string? BalanceDate,
    decimal? Safety, decimal? Lot, decimal? Capacity, int Lead, MqSuggestionRate? Rate,
    decimal?[] Demand, MqSuggestionOrder[] Orders, string[] SourceIds);
public record MqSuggestionDay(string Date, decimal? Demand, decimal Inbound, decimal? Opening,
    decimal? Shortfall, decimal? Buy, decimal? Cost, decimal? Closing, bool CapacityRisk);
public record MqSuggestionResult(string Rule, MqSuggestionInput Input, string PeriodStart, string PeriodEnd,
    MqSuggestionDay[] Days, decimal? Tons, decimal? Cost, int PendingDays, int CalculableDays, string Status,
    string Fingerprint);
public record MqSuggestionVersion(Guid Id, int Version, string Created, string User, MqSuggestionResult Result);

/// <summary>RN-01. Only a consecutive sequence of confirmed days has a calculable balance.</summary>
public static class MaterialIqSuggestions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static MqSuggestionResult Calculate(MqSuggestionInput input)
    {
        if (input.Demand.Length != 7) throw new ArgumentException("Exactly seven demand days are required.");
        var cutoff = DateOnly.ParseExact(input.Cutoff, "yyyy-MM-dd");
        var valid = input.Inventory != null && input.Inventory >= 0 && input.Safety >= 0 && input.Lot > 0;
        decimal? balance = valid ? input.Inventory : null;
        decimal tons = 0, cost = 0;
        var missingCost = false;
        var days = new List<MqSuggestionDay>();
        foreach (var k in Enumerable.Range(0, 7))
        {
            var date = cutoff.AddDays(k + 1).ToString("yyyy-MM-dd");
            var demand = input.Demand[k];
            var inbound = input.Orders.Where(o => o.Arrival == date).Sum(o => o.Qty);
            var opening = balance;
            decimal? shortfall = null, buy = null, dayCost = null, closing = null;
            bool capacityRisk = false;
            if (balance != null && demand != null)
            {
                shortfall = Math.Max(0, demand.Value + input.Safety!.Value - balance.Value - inbound);
                buy = Math.Ceiling(shortfall.Value / input.Lot!.Value) * input.Lot.Value;
                var beforeUse = balance.Value + inbound + buy.Value;
                closing = beforeUse - demand.Value;
                capacityRisk = input.Capacity != null && beforeUse > input.Capacity;
                dayCost = buy == 0 ? 0 : input.Rate == null ? null : buy * (input.Rate.Product + input.Rate.Freight);
                tons += buy.Value;
                if (dayCost == null) missingCost = true; else cost += dayCost.Value;
            }
            // Never replace missing demand with zero; subsequent balances remain unknown.
            balance = closing;
            days.Add(new(date, demand, inbound, opening, shortfall, buy, dayCost, closing, capacityRisk));
        }
        var pending = input.Demand.Count(d => d == null);
        var calculable = days.Count(d => d.Buy != null);
        var status = !valid ? "Data pending" : pending > 0 && tons == 0 ? "Demand pending" :
            missingCost ? "Rate pending" : pending > 0 ? "Provisional" : tons == 0 ? "No purchase needed" : "Purchase suggested";
        decimal? totalTons = !valid || (pending > 0 && tons == 0) ? null : tons;
        decimal? totalCost = totalTons == null || missingCost ? null : cost;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("RN-01/v1:" + JsonSerializer.Serialize(input, Json))));
        return new("RN-01", input, cutoff.AddDays(1).ToString("yyyy-MM-dd"), cutoff.AddDays(7).ToString("yyyy-MM-dd"),
            days.ToArray(), totalTons, totalCost, pending, calculable, status, fingerprint);
    }

    static string S(JsonObject data, string key) => data[key]?.ToString() ?? "";
    static decimal? N(JsonObject data, string key) => data[key] is JsonValue v && v.TryGetValue<decimal>(out var n) ? n : null;
    static bool B(JsonObject data, string key) => data[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string Key(JsonObject d) => S(d, "plant") + "\u001f" + S(d, "sku");
    static bool OnOrBefore(JsonObject d, string cutoff) => string.CompareOrdinal(S(d, "date"), cutoff) <= 0;

    /// <summary>Replay confirmed physical balances, receipts and actual usage; planning never changes stock.</summary>
    public static List<MqSuggestionInput> BuildInputs(JsonObject seed, IReadOnlyList<MqModelEvent> source, string cutoff)
    {
        var events = source.Where(e => e.Type != "suggestionEvaluation").OrderBy(e => e.Created).ThenBy(e => e.Id).ToArray();
        var inventories = ((JsonArray)seed["inventories"]!).OfType<JsonObject>().ToDictionary(Key, d => (JsonObject)d.DeepClone());
        foreach (var e in events.Where(e => e.Type is "catalogPlant" or "catalogSku" or "catalogAssign"))
        {
            var d = e.Data;
            var ps = e.Type == "catalogSku" ? ((JsonArray?)d["plants"])?.Select(p => p!.ToString()) ?? [] : [S(d, "plant")];
            var ss = e.Type == "catalogPlant" ? ((JsonArray?)d["skus"])?.Select(p => p!.ToString()) ?? [] : [S(d, "sku")];
            foreach (var plant in ps) foreach (var sku in ss)
            {
                var inventory = new JsonObject { ["plant"] = plant, ["sku"] = sku, ["date"] = S(d, "date"),
                    ["qty"] = 0, ["safety"] = 0, ["lot"] = 25, ["lead"] = 1 };
                inventories.TryAdd(Key(inventory), inventory);
            }
        }
        var purchases = new List<(Guid Id, JsonObject Data, bool Received, string ReceiptDate, string[] SourceIds)>();
        foreach (var e in events.Where(e => e.Type == "purchase"))
        {
            var d = (JsonObject)e.Data.DeepClone(); var voided = false; var references = new List<string> { e.Id.ToString() };
            foreach (var change in events.Where(c => S(c.Data, "order") == e.Id.ToString() && OnOrBefore(c.Data, cutoff)))
            {
                if (change.Type == "purchaseEdit" && change.Data["replacement"] is JsonObject replacement)
                    foreach (var property in replacement) d[property.Key] = property.Value?.DeepClone();
                if (change.Type == "purchaseVoid") voided = B(change.Data, "voided");
                references.Add(change.Id.ToString());
            }
            if (voided || !OnOrBefore(d, cutoff)) continue;
            var receipt = events.FirstOrDefault(c => c.Type == "receipt" && S(c.Data, "order") == e.Id.ToString() && OnOrBefore(c.Data, cutoff));
            var received = S(d, "status") == "received" || receipt != null;
            if (receipt != null) references.Add(receipt.Id.ToString());
            purchases.Add((e.Id, d, received, S(d, "status") == "received" ? S(d, "date") : receipt == null ? "" : S(receipt.Data, "date"), references.ToArray()));
        }
        var result = new List<MqSuggestionInput>();
        foreach (var inventory in inventories.Values.Where(i => OnOrBefore(i, cutoff)).OrderBy(i => S(i, "plant")).ThenBy(i => S(i, "sku")))
        {
            var key = Key(inventory); var related = events.Where(e => Key(e.Data) == key).ToArray();
            MqModelEvent? Effective(string type) => related.Where(e => e.Type == type && OnOrBefore(e.Data, cutoff))
                .OrderBy(e => S(e.Data, "date"), StringComparer.Ordinal).ThenBy(e => e.Created).ThenBy(e => e.Id).LastOrDefault();
            var snapshot = related.Where(e => (e.Type is "snapshot" or "inventoryAdjustment") && OnOrBefore(e.Data, cutoff) && MaterialIqInventoryValue.ValidCheckpoint(e.Type,e.Data))
                .OrderBy(e => S(e.Data,"date"),StringComparer.Ordinal).ThenBy(e => e.Created).ThenBy(e => e.Id).LastOrDefault();
            var policy = Effective("config");
            var balanceDate = snapshot == null ? S(inventory, "date") : S(snapshot.Data, "date");
            var qty = MaterialIqInventoryValue.Replay(inventory,events,cutoff).Qty;
            var references = new List<string>();
            if (snapshot != null) references.Add(snapshot.Id.ToString());
            if (policy != null) references.Add(policy.Id.ToString());
            var orders = purchases.Where(p => Key(p.Data) == key).ToArray();
            foreach (var p in orders.Where(p => p.Received && string.CompareOrdinal(p.ReceiptDate, balanceDate) > 0)) {
                references.AddRange(p.SourceIds);
            }
            foreach (var usage in related.Where(e => e.Type == "usage" && string.CompareOrdinal(S(e.Data, "date"), balanceDate) > 0 && OnOrBefore(e.Data, cutoff))) {
                references.Add(usage.Id.ToString());
            }
            references.AddRange(related.Where(e=>e.Type=="quantityAdjustment" && OnOrBefore(e.Data,cutoff) &&
                (string.CompareOrdinal(S(e.Data,"date"),balanceDate)>0 || S(e.Data,"date")==balanceDate && (snapshot==null || e.Created>snapshot.Created))).Select(e=>e.Id.ToString()));
            var inbound = orders.Where(p => !p.Received).Select(p => new MqSuggestionOrder(p.Id, S(p.Data, "arrival"), N(p.Data, "qty") ?? 0))
                .Where(o => string.CompareOrdinal(o.Arrival, cutoff) > 0 && string.CompareOrdinal(o.Arrival, DateOnly.ParseExact(cutoff, "yyyy-MM-dd").AddDays(7).ToString("yyyy-MM-dd")) <= 0)
                .OrderBy(o => o.Arrival).ThenBy(o => o.Id).ToArray();
            foreach (var p in orders.Where(p => inbound.Any(o => o.Id == p.Id))) references.AddRange(p.SourceIds);
            var rateRows = ((JsonArray)seed["rates"]!).OfType<JsonObject>().Where(d => Key(d) == key && OnOrBefore(d, cutoff)).Select(d => (JsonObject)d.DeepClone()).ToList();
            foreach (var e in related.Where(e => e.Type == "rate" && OnOrBefore(e.Data, cutoff)).OrderBy(e => S(e.Data, "date")).ThenBy(e => e.Created).ThenBy(e => e.Id)) {
                rateRows.RemoveAll(d => S(d, "supplier") == S(e.Data, "supplier")); rateRows.Add(e.Data); references.Add(e.Id.ToString());
            }
            var available = rateRows.Where(d => S(d, "unit").Equals("ton", StringComparison.OrdinalIgnoreCase) && N(d, "product") >= 0 && N(d, "freight") >= 0)
                .OrderBy(d => N(d, "product") + N(d, "freight")).ThenBy(d => S(d, "supplier"), StringComparer.Ordinal).ToArray();
            var choice = related.LastOrDefault(e => e.Type == "supplyChoice" && S(e.Data, "date") == cutoff);
            if (choice != null) references.Add(choice.Id.ToString());
            var selected = available.FirstOrDefault(d => choice != null && S(d, "supplier") == S(choice.Data, "supplier")) ?? available.FirstOrDefault();
            var rate = selected == null ? null : new MqSuggestionRate(S(selected, "id"), S(selected, "supplier"), N(selected, "product")!.Value,
                N(selected, "freight")!.Value, B(selected, "approved"), S(selected, "date"));
            var demand = Enumerable.Range(1, 7).Select(k => {
                var day = DateOnly.ParseExact(cutoff, "yyyy-MM-dd").AddDays(k).ToString("yyyy-MM-dd");
                var entry = related.LastOrDefault(e => e.Type == "plan" && S(e.Data, "date") == day);
                if (entry != null) references.Add(entry.Id.ToString()); return entry == null ? null : N(entry.Data, "qty");
            }).ToArray();
            var settings = policy?.Data ?? inventory;
            result.Add(new(cutoff, S(inventory, "plant"), S(inventory, "sku"), qty, balanceDate, N(settings, "safety"), N(settings, "lot"),
                N(settings, "capacity"), (int)(N(settings, "lead") ?? 0), rate, demand, inbound, references.Distinct().Order().ToArray()));
        }
        return result;
    }
}
