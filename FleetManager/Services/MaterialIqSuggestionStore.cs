using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace FleetManager.Services;

/// <summary>Append-only RN-01 versions in the existing model event ledger. No destructive migration.</summary>
public static class MaterialIqSuggestionStore
{
    public record State(JsonObject Seed, List<MqModelEvent> Events);

    public static async Task LockAsync(NpgsqlConnection cn, NpgsqlTransaction tx, CancellationToken ct)
    {
        // All event writes and evaluations share the lock, so input snapshots and versions are atomic.
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(73194201)", cn, tx);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<State> LoadAsync(NpgsqlConnection cn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var seedCommand = new NpgsqlCommand("SELECT data::text FROM materialiq.model_reference_seed WHERE id=1", cn, tx);
        var seedText = await seedCommand.ExecuteScalarAsync(ct) as string;
        if (seedText == null) throw new InvalidOperationException("MaterialIQ reference inventory is not installed.");
        var events = new List<MqModelEvent>();
        await using var command = new NpgsqlCommand("SELECT id,type,data::text,created FROM materialiq.model_event ORDER BY created,id", cn, tx);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            events.Add(new(reader.GetGuid(0), reader.GetString(1), JsonNode.Parse(reader.GetString(2))!.AsObject(), reader.GetDateTime(3)));
        return new(JsonNode.Parse(seedText)!.AsObject(), events);
    }

    public static IEnumerable<MqSuggestionVersion> Versions(State state) => state.Events.Where(e => e.Type == "suggestionEvaluation")
        .Select(e => e.Data.Deserialize<MqSuggestionVersion>(MaterialIqSuggestions.Json)!);

    public static async Task<List<MqSuggestionVersion>> EvaluateAsync(NpgsqlConnection cn, NpgsqlTransaction tx, State state,
        string cutoff, string user, CancellationToken ct)
    {
        var saved = Versions(state).Where(v => v.Result.Input.Cutoff == cutoff).ToArray();
        var result = new List<MqSuggestionVersion>();
        foreach (var input in MaterialIqSuggestions.BuildInputs(state.Seed, state.Events, cutoff))
        {
            var calculation = MaterialIqSuggestions.Calculate(input);
            var previous = saved.Where(v => v.Result.Input.Plant == input.Plant && v.Result.Input.Sku == input.Sku).OrderBy(v => v.Version).LastOrDefault();
            if (previous != null && previous.Result.Fingerprint == calculation.Fingerprint) { result.Add(previous); continue; }
            var version = new MqSuggestionVersion(Guid.NewGuid(), (previous?.Version ?? 0) + 1, DateTime.UtcNow.ToString("O"), user, calculation);
            var data = JsonSerializer.SerializeToNode(version, MaterialIqSuggestions.Json)!.AsObject();
            await using var command = new NpgsqlCommand("INSERT INTO materialiq.model_event(id,type,data,actor_username) VALUES(@id,'suggestionEvaluation',@data,@actor)", cn, tx);
            command.Parameters.AddWithValue("id", version.Id);
            command.Parameters.AddWithValue("data", NpgsqlDbType.Jsonb, data.ToJsonString());
            command.Parameters.AddWithValue("actor", user);
            await command.ExecuteNonQueryAsync(ct);
            state.Events.Add(new(version.Id, "suggestionEvaluation", data, DateTime.UtcNow));
            result.Add(version);
        }
        return result;
    }

    public static async Task RecalculateAsync(NpgsqlConnection cn, NpgsqlTransaction tx, State state, string user, CancellationToken ct)
    {
        var cutoffs = Versions(state).Select(v => v.Result.Input.Cutoff).Distinct().ToArray();
        foreach (var cutoff in cutoffs) await EvaluateAsync(cn, tx, state, cutoff, user, ct);
    }

    public static string? ValidatePurchaseLink(State state, JsonObject purchase)
    {
        if (purchase["suggestionId"] == null) return null; // Independently recorded purchases remain valid.
        if (!Guid.TryParse(purchase["suggestionId"]!.ToString(), out var id)) return "Invalid suggestion version.";
        var version = Versions(state).FirstOrDefault(v => v.Id == id);
        if (version == null || version.Result.Input.Plant != purchase["plant"]?.ToString() || version.Result.Input.Sku != purchase["sku"]?.ToString())
            return "The suggestion version must match this plant and material.";
        if (version.Result.Tons is not > 0 || version.Result.Cost == null)
            return "Review a calculable purchase suggestion before recording this order.";
        return null;
    }
}
