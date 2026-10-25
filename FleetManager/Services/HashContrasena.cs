using System.Security.Cryptography;

namespace FleetManager.Services;

/// <summary>
/// Password hashing with PBKDF2-HMAC-SHA256. Stored format:
///     PBKDF2$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;
/// The same format is produced by db/seguridad_esquema.sql for the seed admin.
/// </summary>
public static class HashContrasena
{
    private const int Iteraciones = 100_000;
    private const int TamanoSal = 16;   // bytes
    private const int TamanoHash = 32;  // bytes

    public static string Crear(string contrasena)
    {
        var sal = RandomNumberGenerator.GetBytes(TamanoSal);
        var hash = Derivar(contrasena, sal, Iteraciones);
        return $"PBKDF2${Iteraciones}${Convert.ToBase64String(sal)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verificar(string contrasena, string almacenado)
    {
        if (string.IsNullOrWhiteSpace(almacenado)) return false;
        var p = almacenado.Split('$');
        if (p.Length != 4 || p[0] != "PBKDF2") return false;
        if (!int.TryParse(p[1], out var iteraciones) || iteraciones <= 0) return false;

        byte[] sal, esperado;
        try { sal = Convert.FromBase64String(p[2]); esperado = Convert.FromBase64String(p[3]); }
        catch (FormatException) { return false; }

        var actual = Derivar(contrasena, sal, iteraciones);
        return CryptographicOperations.FixedTimeEquals(actual, esperado);
    }

    private static byte[] Derivar(string contrasena, byte[] sal, int iteraciones) =>
        Rfc2898DeriveBytes.Pbkdf2(contrasena, sal, iteraciones, HashAlgorithmName.SHA256, TamanoHash);
}
