using System.Text.RegularExpressions;
using FleetManager.Models;
using Npgsql;

namespace FleetManager.Services;

/// <summary>
/// Npgsql implementation of login and user maintenance (schema "seguridad").
/// Writes run inside a transaction that sets app.usuario / app.ip so the audit
/// triggers record who changed each row and from where.
/// </summary>
public class SeguridadRepositorioPostgres : ISeguridadRepositorio
{
    private readonly string _cadenaConexion;
    private readonly ILogger<SeguridadRepositorioPostgres> _log;

    public SeguridadRepositorioPostgres(IConfiguration config, ILogger<SeguridadRepositorioPostgres> log)
    {
        _cadenaConexion = FlotaRepositorioPostgres.ConstruirCadenaConexion(config);
        _log = log;
    }

    // --- Input validation (reject strange / unexpected characters) ---------
    // Username (login): 3-60 letters or digits, dot, underscore or hyphen. No spaces.
    private static readonly Regex ReUsername = new(@"^[A-Za-z0-9._\-]{3,60}$", RegexOptions.Compiled);
    // First / last name: must start with a letter; letters (any language),
    // spaces, hyphen, apostrophe and abbreviation dot only.
    private static readonly Regex ReName = new(@"^\p{L}[\p{L} '\-.]{0,79}$", RegexOptions.Compiled);
    // Email: simple but strict; no spaces or angle brackets.
    private static readonly Regex ReEmail = new(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$", RegexOptions.Compiled);
    // Phone (optional): digits, spaces and + - ( ) only.
    private static readonly Regex RePhone = new(@"^[0-9 +()\-]{5,40}$", RegexOptions.Compiled);
    // Document number (optional): letters, digits, hyphen and dot.
    private static readonly Regex ReDocument = new(@"^[\p{L}\p{N}.\-]{1,40}$", RegexOptions.Compiled);

    private async Task<NpgsqlConnection> AbrirAsync(CancellationToken ct)
    {
        var cn = new NpgsqlConnection(_cadenaConexion);
        await cn.OpenAsync(ct);
        return cn;
    }

    private static async Task ContextoAsync(NpgsqlConnection cn, NpgsqlTransaction tx, string usuario, string? ip, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.usuario', @u, true), set_config('app.ip', @ip, true)", cn, tx);
        cmd.Parameters.AddWithValue("u", usuario);
        cmd.Parameters.AddWithValue("ip", (object?)ip ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------
    public async Task VerificarConexionAsync(CancellationToken ct = default)
    {
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM seguridad.app_user WHERE is_active", cn);
        var n = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        _log.LogInformation("Security module connected. Active users: {n}", n);
    }

    // ------------------------------------------------------------------
    // Authentication (by username)
    // ------------------------------------------------------------------
    public async Task<AppUser?> AutenticarAsync(string username, string contrasena, CancellationToken ct = default)
    {
        username = (username ?? "").Trim();
        if (username.Length == 0 || string.IsNullOrEmpty(contrasena)) return null;

        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id_user, username, first_name, last_name, email, phone, document_number, is_admin, password_hash
            FROM seguridad.app_user
            WHERE lower(username) = lower(@u) AND is_active
            LIMIT 1", cn);
        cmd.Parameters.AddWithValue("u", username);

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;

        var hash = rd.GetString(8);
        if (!HashContrasena.Verificar(contrasena, hash)) return null;

        return new AppUser
        {
            IdUser = rd.GetInt64(0),
            Username = rd.GetString(1),
            FirstName = rd.GetString(2),
            LastName = rd.GetString(3),
            Email = rd.GetString(4),
            Phone = rd.IsDBNull(5) ? null : rd.GetString(5),
            DocumentNumber = rd.IsDBNull(6) ? null : rd.GetString(6),
            IsAdmin = rd.GetBoolean(7)
        };
    }

    // ------------------------------------------------------------------
    // Read
    // ------------------------------------------------------------------
    public async Task<List<AppUser>> ListarUsuariosAsync(CancellationToken ct = default)
    {
        var lista = new List<AppUser>();
        await using var cn = await AbrirAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id_user, username, first_name, last_name, email, phone, document_number,
                   is_admin, is_active, created_at, created_by, updated_at, updated_by
            FROM seguridad.app_user
            WHERE is_active
            ORDER BY lower(first_name), lower(last_name)", cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            lista.Add(new AppUser
            {
                IdUser = rd.GetInt64(0),
                Username = rd.GetString(1),
                FirstName = rd.GetString(2),
                LastName = rd.GetString(3),
                Email = rd.GetString(4),
                Phone = rd.IsDBNull(5) ? null : rd.GetString(5),
                DocumentNumber = rd.IsDBNull(6) ? null : rd.GetString(6),
                IsAdmin = rd.GetBoolean(7),
                IsActive = rd.GetBoolean(8),
                CreatedAt = rd.IsDBNull(9) ? null : rd.GetDateTime(9),
                CreatedBy = rd.IsDBNull(10) ? null : rd.GetString(10),
                UpdatedAt = rd.IsDBNull(11) ? null : rd.GetDateTime(11),
                UpdatedBy = rd.IsDBNull(12) ? null : rd.GetString(12)
            });
        return lista;
    }

    // ------------------------------------------------------------------
    // Create
    // ------------------------------------------------------------------
    public async Task<Resultado> CrearUsuarioAsync(AppUserRequest req, string usuario, string? ip, CancellationToken ct = default)
    {
        var val = Validar(req, nuevo: true, out var user, out var fn, out var ln, out var email, out var phone, out var doc);
        if (val != null) return Resultado.Mal(val);

        var hash = HashContrasena.Crear(req.Password!.Trim());

        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ContextoAsync(cn, tx, usuario, ip, ct);

        if (await ExisteUsernameAsync(cn, tx, user, null, ct))
            return Resultado.Mal("That username is already taken.");
        if (await ExisteEmailAsync(cn, tx, email, null, ct))
            return Resultado.Mal("A user with that email already exists.");

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO seguridad.app_user
                (username, first_name, last_name, email, phone, document_number, password_hash, is_admin, is_active)
            VALUES (@u, @fn, @ln, @e, @ph, @doc, @hash, @adm, TRUE)
            RETURNING id_user", cn, tx);
        cmd.Parameters.AddWithValue("u", user);
        cmd.Parameters.AddWithValue("fn", fn);
        cmd.Parameters.AddWithValue("ln", ln);
        cmd.Parameters.AddWithValue("e", email);
        cmd.Parameters.AddWithValue("ph", (object?)phone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("doc", (object?)doc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("hash", hash);
        cmd.Parameters.AddWithValue("adm", req.IsAdmin);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

        await tx.CommitAsync(ct);
        return Resultado.Bien("User created.", new { idUser = id });
    }

    // ------------------------------------------------------------------
    // Update
    // ------------------------------------------------------------------
    public async Task<Resultado> ActualizarUsuarioAsync(AppUserRequest req, string usuario, string? ip, CancellationToken ct = default)
    {
        if (req.IdUser is not > 0) return Resultado.Mal("User not specified.");
        var val = Validar(req, nuevo: false, out var user, out var fn, out var ln, out var email, out var phone, out var doc);
        if (val != null) return Resultado.Mal(val);

        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ContextoAsync(cn, tx, usuario, ip, ct);

        var eraAdmin = await EsAdminAsync(cn, tx, req.IdUser.Value, ct);
        if (eraAdmin is null) return Resultado.Mal("User not found.");
        // Do not leave the system without any administrator.
        if (eraAdmin == true && !req.IsAdmin && await ContarAdminsAsync(cn, tx, ct) <= 1)
            return Resultado.Mal("There must be at least one administrator.");

        if (await ExisteUsernameAsync(cn, tx, user, req.IdUser, ct))
            return Resultado.Mal("That username is already taken.");
        if (await ExisteEmailAsync(cn, tx, email, req.IdUser, ct))
            return Resultado.Mal("Another user already uses that email.");

        var sql = @"UPDATE seguridad.app_user
                       SET username = @u, first_name = @fn, last_name = @ln, email = @e,
                           phone = @ph, document_number = @doc, is_admin = @adm";
        var cambiaClave = !string.IsNullOrWhiteSpace(req.Password);
        if (cambiaClave) sql += ", password_hash = @hash";
        sql += " WHERE id_user = @id AND is_active";

        await using var cmd = new NpgsqlCommand(sql, cn, tx);
        cmd.Parameters.AddWithValue("u", user);
        cmd.Parameters.AddWithValue("fn", fn);
        cmd.Parameters.AddWithValue("ln", ln);
        cmd.Parameters.AddWithValue("e", email);
        cmd.Parameters.AddWithValue("ph", (object?)phone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("doc", (object?)doc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("adm", req.IsAdmin);
        cmd.Parameters.AddWithValue("id", req.IdUser.Value);
        if (cambiaClave) cmd.Parameters.AddWithValue("hash", HashContrasena.Crear(req.Password!.Trim()));
        var filas = await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
        return filas > 0 ? Resultado.Bien("User updated.") : Resultado.Mal("User not found.");
    }

    // ------------------------------------------------------------------
    // Delete (soft)
    // ------------------------------------------------------------------
    public async Task<Resultado> EliminarUsuarioAsync(long idUser, long idActual, string usuario, string? ip, CancellationToken ct = default)
    {
        if (idUser <= 0) return Resultado.Mal("User not specified.");
        if (idUser == idActual) return Resultado.Mal("You cannot delete your own account.");

        await using var cn = await AbrirAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ContextoAsync(cn, tx, usuario, ip, ct);

        var esAdmin = await EsAdminAsync(cn, tx, idUser, ct);
        if (esAdmin is null) return Resultado.Mal("User not found.");
        if (esAdmin == true && await ContarAdminsAsync(cn, tx, ct) <= 1)
            return Resultado.Mal("There must be at least one administrator.");

        await using var cmd = new NpgsqlCommand(
            "UPDATE seguridad.app_user SET is_active = FALSE WHERE id_user = @id AND is_active", cn, tx);
        cmd.Parameters.AddWithValue("id", idUser);
        var filas = await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
        return filas > 0 ? Resultado.Bien("User removed.") : Resultado.Mal("User not found.");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private static async Task<bool> ExisteUsernameAsync(NpgsqlConnection cn, NpgsqlTransaction tx, string username, long? exceptoId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT 1 FROM seguridad.app_user
            WHERE lower(username) = lower(@u) AND is_active AND (@id::bigint IS NULL OR id_user <> @id)
            LIMIT 1", cn, tx);
        cmd.Parameters.AddWithValue("u", username);
        cmd.Parameters.AddWithValue("id", (object?)exceptoId ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private static async Task<bool> ExisteEmailAsync(NpgsqlConnection cn, NpgsqlTransaction tx, string email, long? exceptoId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT 1 FROM seguridad.app_user
            WHERE lower(email) = lower(@e) AND is_active AND (@id::bigint IS NULL OR id_user <> @id)
            LIMIT 1", cn, tx);
        cmd.Parameters.AddWithValue("e", email);
        cmd.Parameters.AddWithValue("id", (object?)exceptoId ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private static async Task<bool?> EsAdminAsync(NpgsqlConnection cn, NpgsqlTransaction tx, long idUser, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT is_admin FROM seguridad.app_user WHERE id_user = @id AND is_active", cn, tx);
        cmd.Parameters.AddWithValue("id", idUser);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r == null ? null : (bool)r;
    }

    private static async Task<long> ContarAdminsAsync(NpgsqlConnection cn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM seguridad.app_user WHERE is_admin AND is_active", cn, tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>Validates and normalizes the form. Returns an error message, or null when valid.</summary>
    private static string? Validar(AppUserRequest req, bool nuevo, out string username,
        out string firstName, out string lastName, out string email, out string? phone, out string? document)
    {
        username = (req.Username ?? "").Trim();
        firstName = (req.FirstName ?? "").Trim();
        lastName = (req.LastName ?? "").Trim();
        email = (req.Email ?? "").Trim();
        phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();
        document = string.IsNullOrWhiteSpace(req.DocumentNumber) ? null : req.DocumentNumber.Trim();

        if (!ReUsername.IsMatch(username)) return "Username must be 3 to 60 letters, numbers, dots, underscores or hyphens.";
        if (!ReName.IsMatch(firstName)) return "First name contains invalid characters.";
        if (!ReName.IsMatch(lastName)) return "Last name contains invalid characters.";
        if (email.Length > 160 || !ReEmail.IsMatch(email)) return "Enter a valid email address.";
        if (phone != null && !RePhone.IsMatch(phone)) return "Phone contains invalid characters.";
        if (document != null && !ReDocument.IsMatch(document)) return "Document number contains invalid characters.";

        var clave = req.Password?.Trim() ?? "";
        var pideClave = nuevo || clave.Length > 0;
        if (pideClave)
        {
            if (clave.Length < 8 || clave.Length > 100) return "Password must be 8 to 100 characters long.";
            if (!clave.Any(char.IsLetter) || !clave.Any(char.IsDigit)) return "Password must contain at least one letter and one number.";
        }
        return null;
    }
}
