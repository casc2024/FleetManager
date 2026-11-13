namespace FleetManager.Models;

public class MqSeleccionReq
{
    public long IdPlant { get; set; }
    public long IdMaterial { get; set; }
    public DateTime Cutoff { get; set; }
    public long? IdSupplier { get; set; }
    public string? Reason { get; set; }
}

public record MqTarifaPlan(long IdSupplier, string Supplier, decimal ProductPriceT, decimal FreightT, bool Approved)
{
    public decimal Delivered => ProductPriceT + FreightT;
}

public record MqDiaPlan(int Day, string Date, decimal? Demand, decimal Inbound,
    decimal? Buy, decimal? Closing, bool CapRisk, bool LeadRisk);

public class MqPlanFila
{
    public long IdPlant { get; set; }
    public long IdMaterial { get; set; }
    public string Plant { get; set; } = "";
    public string MatNumber { get; set; } = "";
    public string Material { get; set; } = "";
    public decimal LotTons { get; set; }
    public List<MqDiaPlan> Dias { get; set; } = new();
    public List<MqTarifaPlan> Rates { get; set; } = new();
    public long? SelectedSupplierId { get; set; }
    public long? AssignedSupplierId { get; set; }
    public string? Supplier { get; set; }
    public decimal? ProductPriceT { get; set; }
    public decimal? FreightT { get; set; }
    public decimal? Delivered { get; set; }
    public bool Approved { get; set; }
    public decimal? WeeklyBuyTn { get; set; }
    public decimal? WeeklyBuyUsd { get; set; }
    public decimal? Savings { get; set; }
    public decimal Demand7Tn { get; set; }
    public decimal? BaselineTn { get; set; }
    public int PendingDays { get; set; }
    public int? FirstDay { get; set; }
    public string? FirstDate { get; set; }
    public bool CapRisk { get; set; }
    public bool LeadRisk { get; set; }
    public string Status { get; set; } = "";
}

/// <summary>Pure calculations shared by the API and scenario checks.</summary>
public static class CalculoInventario
{
    public static (decimal Q, decimal? V) Movimiento(decimal stock, decimal? value, bool receipt, decimal tons, decimal unitCost)
    {
        if (receipt) return (stock + tons, value + tons * unitCost);
        var remaining = stock - tons;
        if (remaining < 0) return (remaining, null); // Surface reconciliation issues instead of hiding excess consumption.
        if (remaining == 0) return (0, 0);
        return (remaining, stock > 0 ? value * remaining / stock : null);
    }

    public static decimal Compra(decimal stock, decimal demand, decimal safety, decimal inbound, decimal lot)
    {
        if (lot <= 0) throw new ArgumentOutOfRangeException(nameof(lot));
        return Math.Ceiling(Math.Max(0, demand + safety - stock - inbound) / lot) * lot;
    }

    // Once a day is pending, subsequent balances cannot be determined reliably.
    public static List<MqDiaPlan> Plan(DateTime cutoff, decimal stock, decimal safety, decimal lot,
        decimal? capacity, int leadDays, IReadOnlyList<decimal?> demand, IReadOnlyList<decimal> inbound)
    {
        var days = new List<MqDiaPlan>();
        decimal? balance = stock;
        for (int i = 0; i < demand.Count; i++)
        {
            decimal? buy = balance != null && demand[i] != null
                ? Compra(balance.Value, demand[i]!.Value, safety, inbound[i], lot) : null;
            var beforeUse = balance + inbound[i] + buy;
            balance = beforeUse - demand[i];
            days.Add(new(i + 1, cutoff.AddDays(i + 1).ToString("yyyy-MM-dd"), demand[i], inbound[i],
                buy, balance, capacity != null && beforeUse > capacity, buy > 0 && leadDays > i + 1));
        }
        return days;
    }
}
