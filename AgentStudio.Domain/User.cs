namespace AgentStudio.Domain;

public enum UserRole
{
    Admin,
    Editor
}

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Username { get; set; }
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Editor;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>True when this account came from appsettings.json ("Users") rather than the
    /// database — not persisted, set only by UserRepository. Blocks role changes/deletion (edit
    /// appsettings.json and restart instead) and lets the /users UI show where an account is
    /// defined, same pattern as ModelProviderConfig.IsFromConfig.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool IsFromConfig { get; set; }

    /// <summary>Set only for a config-sourced account whose appsettings.json entry used the
    /// plaintext "Password" field (dev convenience) instead of a real "PasswordHash". Compared
    /// directly in UserService.VerifyPasswordAsync instead of through PasswordHasher, since
    /// there's no hash to verify against.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? ConfigPlaintextPassword { get; set; }
}

/// <summary>One user account defined in appsettings.json's "Users" array, instead of the
/// database — same operational-config split as DatabaseConnectionConfig/ModelProviderConfig
/// ("ModelProviders"). Set either Password (plaintext, dev convenience — never stored, compared
/// directly) or PasswordHash (a real ASP.NET Core PasswordHasher&lt;User&gt; hash, for
/// production); if both are set, Password wins.</summary>
public sealed class UserConfig
{
    public required string Username { get; set; }
    public string? Password { get; set; }
    public string? PasswordHash { get; set; }
    public UserRole Role { get; set; } = UserRole.Editor;
}
