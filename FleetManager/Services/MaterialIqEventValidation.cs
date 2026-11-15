using System.Globalization;
using System.Text.Json.Nodes;

namespace FleetManager.Services;

public static class MaterialIqEventValidation
{
    public static bool ValidText(string text) => System.Text.RegularExpressions.Regex.IsMatch(text,
        @"^[\p{L}\p{M}\p{N} .,'&()/#:_\-]*$") &&
        (string.IsNullOrWhiteSpace(text) || text.Any(char.IsLetterOrDigit));
    public static string? Validate(string type, JsonObject data)
    {
        string? Text(string key, bool required = false)
        {
            if (data[key] is null) return required ? $"{key} is required." : null;
            if (data[key] is not JsonValue v || !v.TryGetValue<string>(out var text) ||
                text.Length > 300 || !ValidText(text) || (required && string.IsNullOrWhiteSpace(text)))
                return $"Invalid {key}.";
            if (key == "ref" && !System.Text.RegularExpressions.Regex.IsMatch(text, @"^[\p{L}\p{N} ._/\-]*$"))
                return "Ticket references allow only letters, numbers, spaces, hyphens, slashes, dots and underscores.";
            return null;
        }
        foreach (var key in new[] { "plant", "sku", "supplier", "ref", "reason", "unit", "source", "origin" })
            if (Text(key) is { } error) return error;
        foreach (var key in new[] { "date", "arrival" })
        {
            if (key == "arrival" && data[key] is null) continue;
            if (data[key] is not JsonValue v || !v.TryGetValue<string>(out var text) ||
                !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return $"Invalid {key}.";
        }
        var required = type switch
        {
            "rate" or "purchase" => new[] { "plant", "sku", "supplier" },
            "plan" or "snapshot" or "inventoryAdjustment" or "usage" or "config" or "supplyChoice" or "catalogAssign" => new[] { "plant", "sku" },
            "catalogPlant" => new[] { "plant" },
            "catalogSku" => new[] { "sku" },
            "purchaseEdit" or "purchaseVoid" => new[] { "reason" },
            _ => Array.Empty<string>()
        };
        foreach (var key in required) if (Text(key, true) is { } error) return error;
        foreach (var key in new[] { "qty", "product", "freight", "value", "productValue", "freightValue", "safety", "capacity", "lot", "lead", "coefficient" })
        {
            if (data[key] is null) continue;
            if (data[key] is not JsonValue v || !v.TryGetValue<decimal>(out var number) || number < 0 || number > 1_000_000_000m)
                return $"Invalid {key}.";
            if (key == "lead" && decimal.Truncate(number) != number) return "Lead time must be a whole number.";
        }
        var numbers = type switch
        {
            "rate" => new[] { "product", "freight" },
            "purchase" => new[] { "qty", "product", "freight" },
            "plan" or "usage" or "inventoryAdjustment" => new[] { "qty" },
            "snapshot" => new[] { "qty", "value" },
            "config" => new[] { "safety", "lot", "lead" },
            _ => Array.Empty<string>()
        };
        foreach (var key in numbers) if (data[key] is null) return $"{key} is required.";
        if (type == "snapshot")
        {
            var product = data["productValue"]; var freight = data["freightValue"];
            if ((product == null) != (freight == null)) return "Enter both product and transportation costs, or leave both blank.";
            if (product != null && Math.Abs(product.GetValue<decimal>() + freight!.GetValue<decimal>() - data["value"]!.GetValue<decimal>()) > 0.01m)
                return "Product plus transportation must equal the confirmed total within USD 0.01.";
        }
        if (type == "purchase" && (data["qty"]!.GetValue<decimal>() <= 0 || Text("ref", true) != null))
            return "A positive quantity and ticket reference are required.";
        if (type == "config" && (data["lot"]!.GetValue<decimal>() <= 0 ||
            (data["capacity"] != null && data["capacity"]!.GetValue<decimal>() < data["safety"]!.GetValue<decimal>())))
            return "Lot must be positive and capacity cannot be below safety stock.";
        foreach (var key in new[] { "approved", "voided" })
            if (data[key] != null && (data[key] is not JsonValue v || !v.TryGetValue<bool>(out _))) return $"Invalid {key}.";
        if (type == "rate" && data["approved"] is null) return "Approval status is required.";
        if (type is "receipt" or "purchaseEdit" or "purchaseVoid")
            if (data["order"] is not JsonValue order || !order.TryGetValue<string>(out var id) || !Guid.TryParse(id, out _))
                return "A valid purchase reference is required.";
        if (type == "purchaseEdit")
        {
            if (data["replacement"] is not JsonObject replacement) return "Purchase details are required.";
            return Validate("purchase", replacement);
        }
        if (type == "purchase" && (data["status"]?.ToString() is not ("received" or "ordered"))) return "Invalid purchase status.";
        if (data["arrival"] != null && string.CompareOrdinal(data["arrival"]!.ToString(), data["date"]!.ToString()) < 0)
            return "Arrival cannot be before the purchase date.";
        foreach (var key in new[] { "plants", "skus" })
        {
            if (data[key] is null && !((type == "catalogPlant" && key == "skus") || (type == "catalogSku" && key == "plants"))) continue;
            if (data[key] is not JsonArray list || list.Count == 0 || list.Any(x => x is not JsonValue v ||
                !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s) || s.Length > 80 || !ValidText(s)))
                return $"Invalid {key}.";
        }
        return null;
    }
}
