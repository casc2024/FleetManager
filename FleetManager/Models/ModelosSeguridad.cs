namespace FleetManager.Models;

/// <summary>A user of the system (security schema). The password hash is never sent to the UI.</summary>
public class AppUser
{
    public long IdUser { get; set; }
    public string Username { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public string? DocumentNumber { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Create / update payload from the user maintainer form.</summary>
public class AppUserRequest
{
    public long? IdUser { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? DocumentNumber { get; set; }
    public bool IsAdmin { get; set; }
    /// <summary>Required on create; on edit, only changes the password when non-empty.</summary>
    public string? Password { get; set; }
}

/// <summary>Sign-in payload (username + password).</summary>
public class LoginRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
}

/// <summary>A person the quote email can be sent to (recipients maintainer).</summary>
public class Recipient
{
    public long IdRecipient { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class RecipientRequest
{
    public long? IdRecipient { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
}

/// <summary>Send the current quote proposal by email to a saved recipient.</summary>
public class QuoteEmailRequest
{
    public long? RecipientId { get; set; }
    public string? Subject { get; set; }
    public string? Html { get; set; }
    /// <summary>The proposal rendered as a PDF by the browser, base64-encoded (optional).</summary>
    public string? PdfBase64 { get; set; }
    public string? PdfName { get; set; }
}

/// <summary>Who is signed in, for the top menu and to lock the audit identity.</summary>
public class SessionUser
{
    public long IdUser { get; set; }
    public string Username { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public bool IsAdmin { get; set; }
}
