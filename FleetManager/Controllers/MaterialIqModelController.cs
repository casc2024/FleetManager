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

    [HttpPost("events")]
    [RequestSizeLimit(262144)]
    public async Task<IActionResult> Save([FromBody] EventRequest? request,CancellationToken ct)
    {
        string[] allowed=["rate","purchase","purchaseEdit","purchaseVoid","receipt","plan","snapshot","usage","config","supplyChoice","catalogPlant","catalogSku","catalogAssign"];
        if(request==null||!Guid.TryParse(request.Id,out var id)||!allowed.Contains(request.Type)||request.Data.ValueKind!=JsonValueKind.Object)
            return BadRequest(new {error="Invalid MaterialIQ business record."});
        var data=JsonNode.Parse(request.Data.GetRawText())!.AsObject();
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
        await using var cmd=new NpgsqlCommand(@"INSERT INTO materialiq.model_event(id,type,data,actor_username)
            VALUES(@id,@type,@data,@actor) ON CONFLICT(id) DO NOTHING",cn);
        cmd.Parameters.AddWithValue("id",id);
        cmd.Parameters.AddWithValue("type",request.Type!);
        cmd.Parameters.AddWithValue("data",NpgsqlDbType.Jsonb,data.ToJsonString());
        cmd.Parameters.AddWithValue("actor",User.Identity?.Name??"unknown");
        await cmd.ExecuteNonQueryAsync(ct);
        return Json(new {ok=true,id});
    }
}
