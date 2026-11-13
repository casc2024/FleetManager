using System.Security.Claims;
using FleetManager.Models;

namespace FleetManager.Services;

/// <summary>Helpers to read the signed-in user and the client IP inside controllers.</summary>
public static class IdentidadApp
{
    public const string RolAdmin = "Admin";

    public static SessionUser? Usuario(this ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return null;
        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!long.TryParse(id, out var idUser)) return null;
        return new SessionUser
        {
            IdUser = idUser,
            Username = user.FindFirst(ClaimTypes.Name)?.Value ?? "",
            Name = user.FindFirst("FullName")?.Value ?? "",
            Email = user.FindFirst(ClaimTypes.Email)?.Value ?? "",
            IsAdmin = user.IsInRole(RolAdmin),
            CanQuotes = user.IsInRole(RolAdmin) || user.HasClaim("perm", "quotes"),
            CanMaterialiq = user.IsInRole(RolAdmin) || user.HasClaim("perm", "materialiq")
        };
    }

    public static string? Ip(this HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip == null) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }

    public static List<Claim> Claims(AppUser u)
    {
        var list = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, u.IdUser.ToString()),
            new(ClaimTypes.Name, u.Username),
            new("FullName", $"{u.FirstName} {u.LastName}".Trim()),
            new(ClaimTypes.Email, u.Email),
            new(ClaimTypes.Role, u.IsAdmin ? RolAdmin : "User")
        };
        // Per-module permissions: let a non-admin reach a specific module.
        if (u.CanQuotes) list.Add(new("perm", "quotes"));
        if (u.CanMaterialiq) list.Add(new("perm", "materialiq"));
        return list;
    }
}
