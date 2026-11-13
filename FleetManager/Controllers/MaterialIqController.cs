using FleetManager.Models;
using FleetManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FleetManager.Controllers;

/// <summary>MaterialIQ: inventory, demand and supply planning (admin only). Schema "materialiq".</summary>
[Authorize(Policy = "MaterialIQ")]
public class MaterialIqController : Controller
{
    private readonly IMaterialIqRepositorio _repo;
    public MaterialIqController(IMaterialIqRepositorio repo) => _repo = repo;

    [HttpGet("/materialiq")]
    public IActionResult Index() => View();

    // ---- reference data ----
    [HttpGet("/api/materialiq/plants")]
    public async Task<IActionResult> Plantas(CancellationToken ct) => Json(await _repo.ListarPlantasAsync(ct));

    [HttpGet("/api/materialiq/materials")]
    public async Task<IActionResult> Materiales(CancellationToken ct) => Json(await _repo.ListarMaterialesAsync(ct));

    [HttpGet("/api/materialiq/suppliers")]
    public async Task<IActionResult> Proveedores(CancellationToken ct) => Json(await _repo.ListarProveedoresAsync(ct));

    [HttpGet("/api/materialiq/policies")]
    public async Task<IActionResult> Politicas([FromQuery] long? plant, CancellationToken ct) => Json(await _repo.ListarPoliticasAsync(plant, ct));

    [HttpGet("/api/materialiq/rates")]
    public async Task<IActionResult> Tarifas([FromQuery] long? plant, CancellationToken ct) => Json(await _repo.ListarTarifasAsync(plant, ct));

    // ---- overview ----
    [HttpGet("/api/materialiq/overview")]
    public async Task<IActionResult> Resumen([FromQuery] DateTime? fecha, [FromQuery] long? plant, CancellationToken ct)
    {
        var corte = (fecha ?? DateTime.Today).Date;
        var filas = await _repo.ObtenerFilasAsync(corte, plant, ct);
        var compras = await _repo.ComprasHastaCorteAsync(corte, plant, ct);

        decimal valorConfirmado = 0, valorReferencia = 0, comprasSugeridas = 0;
        int pendientes = 0;
        var detalle = new List<object>();

        foreach (var f in filas)
        {
            var stock = f.StockTons ?? 0m;
            var demanda = f.DemandTomorrow;
            if (demanda == null) pendientes++;
            if (f.ValueUsd != null) valorConfirmado += f.ValueUsd.Value;
            if (f.BestRateT != null && stock > 0) valorReferencia += stock * f.BestRateT.Value;

            decimal? cobertura = null, diasEq = null;
            bool bajoStock = false;
            decimal compraTn = 0, compraUsd = 0;

            if (demanda is > 0) { cobertura = 100m * stock / demanda.Value; diasEq = stock / demanda.Value; }
            if (demanda != null)
            {
                bajoStock = stock < demanda.Value + f.SafetyTons;
                var gap = Math.Max(0m, demanda.Value + f.SafetyTons - stock - f.InboundTons);
                if (gap > 0 && f.LotTons > 0)
                {
                    compraTn = Math.Ceiling(gap / f.LotTons) * f.LotTons;
                    if (f.BestRateT != null) compraUsd = compraTn * f.BestRateT.Value;
                }
            }
            comprasSugeridas += compraUsd;

            detalle.Add(new
            {
                f.IdPlant, f.Plant, f.IdMaterial, f.MatNumber, f.Material, f.Unit,
                stock = f.StockTons, valor = f.ValueUsd, demanda = f.DemandTomorrow,
                seguridad = f.SafetyTons, lote = f.LotTons, llegadas = f.InboundTons,
                tarifa = f.BestRateT, cobertura, diasEq, bajoStock,
                comprarTn = compraTn, comprarUsd = compraUsd,
                estado = demanda == null ? "pending" : bajoStock ? "low" : "ok"
            });
        }

        return Json(new
        {
            fecha = corte.ToString("yyyy-MM-dd"),
            manana = corte.AddDays(1).ToString("yyyy-MM-dd"),
            resumen = new MqResumen
            {
                ValorConfirmado = valorConfirmado, ValorReferencia = valorReferencia,
                ComprasSugeridasUsd = comprasSugeridas, ComprasHastaCorte = compras, FilasPendientes = pendientes
            },
            filas = detalle
        });
    }

    [HttpGet("/api/materialiq/week")]
    public async Task<IActionResult> Semana([FromQuery] DateTime? fecha, [FromQuery] long? plant, CancellationToken ct)
        => Json(await _repo.Simular7DiasAsync((fecha ?? DateTime.Today).Date, plant, ct));

    [HttpGet("/api/materialiq/demand")]
    public async Task<IActionResult> Demanda([FromQuery] DateTime? fecha, [FromQuery] long? plant, CancellationToken ct)
        => Json(await _repo.ListarDemandaAsync((fecha ?? DateTime.Today.AddDays(1)).Date, plant, ct));

    [HttpGet("/api/materialiq/purchases")]
    public async Task<IActionResult> Compras([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta, [FromQuery] long? plant, CancellationToken ct)
        => Json(await _repo.ListarComprasAsync((desde ?? DateTime.Today.AddDays(-30)).Date, (hasta ?? DateTime.Today).Date, plant, ct));

    [HttpGet("/api/materialiq/charts")]
    public async Task<IActionResult> Charts([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta, [FromQuery] long? plant, [FromQuery] string? modo, CancellationToken ct)
        => Json(await _repo.ChartsAsync((desde ?? DateTime.Today.AddDays(-30)).Date, (hasta ?? DateTime.Today).Date, plant, modo ?? "received", ct));

    [HttpGet("/api/materialiq/history")]
    public async Task<IActionResult> Historial([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta, [FromQuery] string? entity, CancellationToken ct)
        => Json(await _repo.ListarHistorialAsync((desde ?? DateTime.Today.AddDays(-30)).Date, (hasta ?? DateTime.Today).Date, entity, ct));

    // ---- writes ----
    [HttpPost("/api/materialiq/demand")]
    public async Task<IActionResult> GuardarDemanda([FromBody] MqDemandaReq req, CancellationToken ct)
        => R(await _repo.GuardarDemandaAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    [HttpPost("/api/materialiq/purchase")]
    public async Task<IActionResult> CrearCompra([FromBody] MqCompraReq req, CancellationToken ct)
        => R(await _repo.CrearCompraAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    [HttpPost("/api/materialiq/purchase/receive")]
    public async Task<IActionResult> RecibirCompra([FromBody] MqAccionCompraReq req, CancellationToken ct)
        => R(await _repo.RecibirCompraAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    [HttpPost("/api/materialiq/purchase/update")]
    public async Task<IActionResult> EditarCompra([FromBody] MqCompraReq req, CancellationToken ct)
        => R(await _repo.EditarCompraAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    [HttpPost("/api/materialiq/purchase/void")]
    public async Task<IActionResult> AnularCompra([FromBody] MqAccionCompraReq req, CancellationToken ct)
        => R(await _repo.AnularCompraAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    [HttpPost("/api/materialiq/purchase/restore")]
    public async Task<IActionResult> RestaurarCompra([FromBody] MqAccionCompraReq req, CancellationToken ct)
        => R(await _repo.RestaurarCompraAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    [HttpPost("/api/materialiq/balance")]
    public async Task<IActionResult> GuardarSaldo([FromBody] MqSaldoReq req, CancellationToken ct)
        => R(await _repo.GuardarSaldoAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    [HttpPost("/api/materialiq/consumption")]
    public async Task<IActionResult> RegistrarConsumo([FromBody] MqConsumoReq req, CancellationToken ct)
        => R(await _repo.RegistrarConsumoAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    [HttpPost("/api/materialiq/policy")]
    public async Task<IActionResult> GuardarPolitica([FromBody] MqPoliticaReq req, CancellationToken ct)
        => R(await _repo.GuardarPoliticaAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    [HttpPost("/api/materialiq/supplier")]
    public async Task<IActionResult> CrearProveedor([FromBody] MqProveedorReq req, CancellationToken ct)
        => R(await _repo.CrearProveedorAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    [HttpPost("/api/materialiq/rate")]
    public async Task<IActionResult> CrearTarifa([FromBody] MqTarifaReq req, CancellationToken ct)
        => R(await _repo.CrearTarifaAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    [HttpPost("/api/materialiq/rate/approve")]
    public async Task<IActionResult> AprobarTarifa([FromBody] MqIdReq req, CancellationToken ct)
        => R(await _repo.AprobarTarifaAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    [HttpPost("/api/materialiq/plant")]
    public async Task<IActionResult> CrearPlanta([FromBody] MqPlantaReq req, CancellationToken ct)
        => R(await _repo.CrearPlantaAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    [HttpPost("/api/materialiq/material")]
    public async Task<IActionResult> CrearMaterial([FromBody] MqMaterialReq req, CancellationToken ct)
        => R(await _repo.CrearMaterialAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    [HttpPost("/api/materialiq/assign")]
    public async Task<IActionResult> Asignar([FromBody] MqAsignarReq req, CancellationToken ct)
        => R(await _repo.AsignarMaterialAsync(req ?? new(), U(), HttpContext.Ip(), ct));

    private IActionResult R(Resultado r) => r.Ok ? Json(r) : BadRequest(r);
    private string U() => User.Usuario()?.Username ?? "unknown";
}
