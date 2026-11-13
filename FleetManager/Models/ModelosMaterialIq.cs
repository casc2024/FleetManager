namespace FleetManager.Models;

/// <summary>A plant shown in the MaterialIQ filter.</summary>
public class MqPlanta
{
    public long IdPlant { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>One plant + material row of the inventory overview for a cutoff date.</summary>
public class MqFila
{
    public long IdPlant { get; set; }
    public string Plant { get; set; } = "";
    public long IdMaterial { get; set; }
    public string MatNumber { get; set; } = "";
    public string Material { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>Physical tons from the latest closing balance on or before the cutoff date.</summary>
    public decimal? StockTons { get; set; }
    /// <summary>Confirmed USD value of that balance (null = valuation unknown).</summary>
    public decimal? ValueUsd { get; set; }
    /// <summary>Confirmed demand for tomorrow (null = pending, not zero).</summary>
    public decimal? DemandTomorrow { get; set; }
    public decimal SafetyTons { get; set; }
    public decimal? MaxCapacityTons { get; set; }
    public decimal LotTons { get; set; }
    /// <summary>Date of the closing balance that stock/value were replayed from (null if none).</summary>
    public DateTime? BalanceDate { get; set; }
    /// <summary>Confirmed demand over the next 7 days (sum of entered days).</summary>
    public decimal Demand7Tons { get; set; }
    /// <summary>Number of the next 7 days still pending (no demand entered).</summary>
    public int Demand7Pending { get; set; }
    /// <summary>Tons on purchase orders still in transit (not yet physical stock).</summary>
    public decimal InboundTons { get; set; }
    /// <summary>Lowest approved delivered rate ($/t = product + freight), null if none.</summary>
    public decimal? BestRateT { get; set; }
    /// <summary>Product and freight components of that cheapest rate (for the two-tone value bar).</summary>
    public decimal? BestProductT { get; set; }
    public decimal? BestFreightT { get; set; }
    /// <summary>Whether the cheapest effective rate is approved (A2: the reference may use an unapproved rate).</summary>
    public bool BestRateApproved { get; set; }
}

/// <summary>Cards at the top of the overview.</summary>
public class MqResumen
{
    public decimal ValorConfirmado { get; set; }
    public decimal ValorReferencia { get; set; }
    public decimal ComprasSugeridasUsd { get; set; }
    public decimal ComprasHastaCorte { get; set; }
    public int FilasPendientes { get; set; }
}

// ---- Request payloads (capture) ----------------------------------------

public class MqDemandaReq
{
    public long IdPlant { get; set; }
    public long IdMaterial { get; set; }
    public DateTime Fecha { get; set; }
    public decimal? Tons { get; set; }
    public decimal? ProduccionCy { get; set; }
    public decimal? DosisTCy { get; set; }
}

public class MqCompraReq
{
    public long? IdPurchase { get; set; }
    public long IdPlant { get; set; }
    public long IdMaterial { get; set; }
    public long? IdSupplier { get; set; }
    public DateTime PurchaseDate { get; set; }
    public decimal Tons { get; set; }
    public decimal ProductPriceT { get; set; }
    public decimal FreightT { get; set; }
    public DateTime? ExpectedDate { get; set; }
    public string? Status { get; set; }          // In transit / Received
    public DateTime? ReceivedDate { get; set; }
    public string? TicketRef { get; set; }
    public string? Reason { get; set; }
}

public class MqAccionCompraReq
{
    public long IdPurchase { get; set; }
    public DateTime? ReceivedDate { get; set; }
    public string? TicketRef { get; set; }
    public string? Reason { get; set; }
}

public class MqSaldoReq
{
    public long IdPlant { get; set; }
    public long IdMaterial { get; set; }
    public DateTime BalanceDate { get; set; }
    public decimal Tons { get; set; }
    public decimal? ValueUsd { get; set; }
}

public class MqConsumoReq
{
    public long IdPlant { get; set; }
    public long IdMaterial { get; set; }
    public DateTime UseDate { get; set; }
    public decimal Tons { get; set; }
}

public class MqPoliticaReq
{
    public long? AssignedSupplierId { get; set; }
    public long IdPlant { get; set; }
    public long IdMaterial { get; set; }
    public decimal SafetyStockT { get; set; }
    public decimal? MaxCapacityT { get; set; }
    public int LeadTimeDays { get; set; }
    public decimal PurchaseLotT { get; set; }
}

public class MqProveedorReq { public string? Name { get; set; } }

public class MqTarifaReq
{
    public long? IdRate { get; set; }
    public long IdPlant { get; set; }
    public long IdMaterial { get; set; }
    public long IdSupplier { get; set; }
    public DateTime EffectiveDate { get; set; }
    public decimal ProductPriceT { get; set; }
    public decimal FreightT { get; set; }
    public string? Origin { get; set; }
    public bool Approved { get; set; }
}

public class MqPlantaReq { public string? Code { get; set; } public string? Name { get; set; } }

public class MqMaterialReq
{
    public string? MatNumber { get; set; }
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Unit { get; set; }
    public decimal? Density { get; set; }
}

public class MqAsignarReq { public long IdPlant { get; set; } public long IdMaterial { get; set; } }

public class MqIdReq { public long Id { get; set; } }
