using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using FleetManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace FleetManager.Controllers;

/// <summary>PostgreSQL adapter for the original MaterialIQ event model.</summary>
[Authorize(Policy = "MaterialIQ")]
[Route("api/materialiq/model")]
public class MaterialIqModelController : Controller
{
    private readonly string _connection;
    public MaterialIqModelController(IConfiguration configuration)
        => _connection = FlotaRepositorioPostgres.ConstruirCadenaConexion(configuration);

    [HttpGet("me")]
    public IActionResult Me() => Json(new {
        authenticated=true,
        name=User.FindFirst("FullName")?.Value ?? User.Identity?.Name,
        email=User.FindFirst(ClaimTypes.Email)?.Value
    });

    [HttpGet("events")]
    public async Task<IActionResult> Events(CancellationToken ct)
    {
        await using var cn=new NpgsqlConnection(_connection);await cn.OpenAsync(ct);
        await using var cmd=new NpgsqlCommand("SELECT id,type,data::text,created FROM materialiq.model_event ORDER BY created,id",cn);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        var events=new List<object>();
        while(await reader.ReadAsync(ct))
            events.Add(new {id=reader.GetGuid(0).ToString(),type=reader.GetString(1),data=JsonNode.Parse(reader.GetString(2)),created=reader.GetDateTime(3).ToString("O")});
        return Json(events);
    }

    public record EventRequest(string? Id,string? Type,JsonElement Data);
    public record EvaluationRequest(string Cutoff);

    [HttpPost("evaluations")]
    public async Task<IActionResult> Evaluate([FromBody] EvaluationRequest? request, CancellationToken ct)
    {
        if (request == null || !DateOnly.TryParseExact(request.Cutoff, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date) || date.Year < 1900 || date.Year > 9998)
            return BadRequest(new { error = "Enter a valid cutoff date." });
        await using var cn = new NpgsqlConnection(_connection); await cn.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await MaterialIqSuggestionStore.LockAsync(cn, tx, ct);
        var state = await MaterialIqSuggestionStore.LoadAsync(cn, tx, ct);
        var versions = await MaterialIqSuggestionStore.EvaluateAsync(cn, tx, state, request.Cutoff, Actor(), ct);
        var history = MaterialIqSuggestionStore.Versions(state).OrderByDescending(v => v.Created).ToArray();
        await tx.CommitAsync(ct);
        return Json(new { cutoff = request.Cutoff, versions, history });
    }

    private string Actor() => User.FindFirst("FullName")?.Value ?? User.Identity?.Name ?? "User";
    public record ExcelRequest(string Title, string[]? Headers = null, JsonElement[][]? Rows = null, MaterialIqExcel.Sheet[]? Sheets = null);

    [HttpPost("export")]
    [RequestSizeLimit(16_777_216)]
    public IActionResult Export([FromBody] ExcelRequest? request, [FromServices] IWebHostEnvironment environment)
    {
        var sheets = request?.Sheets ?? (request?.Headers != null && request.Rows != null
            ? new[] { new MaterialIqExcel.Sheet(request.Title, request.Headers, request.Rows) } : Array.Empty<MaterialIqExcel.Sheet>());
        if (request == null || !ModelState.IsValid || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 120 ||
            sheets.Length is < 1 or > 10 || sheets.Any(sheet => sheet == null ||
            string.IsNullOrWhiteSpace(sheet.Title) || sheet.Title.Length > 120 || sheet.Headers == null || sheet.Rows == null ||
            sheet.Headers.Length is < 1 or > 100 || sheet.Headers.Any(h => h == null || h.Length > 300) ||
            sheet.Rows.Length > 20000 || sheet.Rows.Any(row => row == null || row.Length != sheet.Headers.Length || row.Any(c =>
                c.ValueKind is JsonValueKind.Object or JsonValueKind.Array || c.ToString().Length > 32767))))
            return BadRequest(new { error = "Invalid Excel report or report too large (maximum 20,000 rows)." });
        var user = User.FindFirst("FullName")?.Value ?? User.Identity?.Name ?? "User";
        var logo = System.IO.File.ReadAllBytes(Path.Combine(environment.WebRootPath, "img", "jck-logo.jpg"));
        var bytes = MaterialIqExcel.Create(user, DateTimeOffset.UtcNow, sheets, logo);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "JCK-MaterialIQ-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".xlsx");
    }

    [HttpPost("events")]
    [RequestSizeLimit(262144)]
    public async Task<IActionResult> Save([FromBody] EventRequest? request,CancellationToken ct)
    {
        string[] allowed=["rate","purchase","purchaseEdit","purchaseVoid","receipt","plan","snapshot","inventoryAdjustment","usage","config","supplyChoice","catalogPlant","catalogSku","catalogAssign"];
        if(request==null||!Guid.TryParse(request.Id,out var id)||!allowed.Contains(request.Type)||request.Data.ValueKind!=JsonValueKind.Object)
            return BadRequest(new {error="Invalid MaterialIQ business record."});
        var data=JsonNode.Parse(request.Data.GetRawText())!.AsObject();
        if (MaterialIqEventValidation.Validate(request.Type!, data) is { } validationError)
            return BadRequest(new { error = validationError });
        foreach(var key in new[]{"qty","product","freight","value","safety","capacity","lot","lead"})
            if(data[key]!=null && (!decimal.TryParse(data[key]!.ToJsonString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var value)||value<0||value>1_000_000_000m))
                return BadRequest(new {error=$"Invalid {key}."});
        if(data["date"] is not JsonValue dateValue || !dateValue.TryGetValue<string>(out var dateText) ||
           !DateOnly.TryParseExact(dateText,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out _))
            return BadRequest(new {error="A valid transaction date is required."});
        // Attribution is always supplied by the signed-in server session.
        data["_auditActor"]=new JsonObject {
            ["id"]=User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            ["name"]=User.FindFirst("FullName")?.Value??User.Identity?.Name,
            ["email"]=User.FindFirst(ClaimTypes.Email)?.Value
        };
        await using var cn=new NpgsqlConnection(_connection);await cn.OpenAsync(ct);
        await using var tx=await cn.BeginTransactionAsync(ct);
        await MaterialIqSuggestionStore.LockAsync(cn, tx, ct);
        var state = await MaterialIqSuggestionStore.LoadAsync(cn, tx, ct);
        if(state.Events.Any(e=>e.Id==id)) return Json(new {ok=true,id});
        if (request.Type == "purchase" && MaterialIqSuggestionStore.ValidatePurchaseLink(state, data) is { } linkError)
            return BadRequest(new { error = linkError });
        if (request.Type == "snapshot")
        {
            var plant = data["plant"]!.ToString(); var sku = data["sku"]!.ToString(); var cutoff = data["date"]!.ToString();
            var initial = MaterialIqInventoryValue.FindInitial(state.Seed, state.Events, plant, sku, cutoff);
            if (initial == null) return BadRequest(new { error = "Select a material assigned to this plant on the closing date." });
            // Never trust caller-supplied before values. Archive the actual cutoff balance atomically.
            data["before"] = JsonSerializer.SerializeToNode(MaterialIqInventoryValue.Replay(initial, state.Events, cutoff), MaterialIqSuggestions.Json);
            var total = data["value"]!.GetValue<decimal>();
            data["unallocatedValue"] = data["productValue"] == null ? total : 0m;
            data["roundingAdjustment"] = data["productValue"] == null ? 0m : total - data["productValue"]!.GetValue<decimal>() - data["freightValue"]!.GetValue<decimal>();
        }
        if(request.Type=="inventoryAdjustment")
        {
            var plant=data["plant"]!.ToString();var sku=data["sku"]!.ToString();var cutoff=data["date"]!.ToString();
            var initial=MaterialIqInventoryValue.FindInitial(state.Seed,state.Events,plant,sku,cutoff);
            if(initial==null) return BadRequest(new {error="Select a material assigned to this plant on the adjustment date."});
            var before=MaterialIqInventoryValue.Replay(initial,state.Events,cutoff);
            JsonObject adjustment;
            try {adjustment=MaterialIqInventoryValue.AdjustTons(before,data["qty"]!.GetValue<decimal>());}
            catch(ArgumentException ex) {return BadRequest(new {error=ex.Message});}
            foreach(var pair in adjustment) data[pair.Key]=pair.Value?.DeepClone();
            data["before"]=JsonSerializer.SerializeToNode(before,MaterialIqSuggestions.Json);
            data["reason"]=string.IsNullOrWhiteSpace(data["reason"]?.ToString()) ? null : data["reason"]!.ToString().Trim();
        }
        if (request.Type == "purchaseEdit" && data["replacement"] is JsonObject replacement)
            replacement.Remove("suggestionId"); // A correction cannot rewrite the origin of the original purchase.
        await using var cmd=new NpgsqlCommand(@"INSERT INTO materialiq.model_event(id,type,data,actor_username,created)
            VALUES(@id,@type,@data,@actor,clock_timestamp()) ON CONFLICT(id) DO NOTHING RETURNING created",cn,tx);
        cmd.Parameters.AddWithValue("id",id);
        cmd.Parameters.AddWithValue("type",request.Type!);
        cmd.Parameters.AddWithValue("data",NpgsqlDbType.Jsonb,data.ToJsonString());
        cmd.Parameters.AddWithValue("actor",User.Identity?.Name??"unknown");
        var inserted = await cmd.ExecuteScalarAsync(ct);
        if (inserted is DateTime created) {
            state.Events.Add(new(id, request.Type!, data, created));
            await MaterialIqSuggestionStore.RecalculateAsync(cn, tx, state, Actor(), ct);
        }
        await tx.CommitAsync(ct);
        return Json(new {ok=true,id});
    }
}
