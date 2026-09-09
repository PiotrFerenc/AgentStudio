namespace AgentStudio.Domain;

public enum UserRole
{
    Admin,
    Editor
}

/// <summary>A user account — always sourced from appsettings.json's "Users" array (see
/// UserRepository), never persisted. Id is deterministic (DeterministicGuid.From the username)
/// since there's nowhere to persist a random one, and Agent.OwnerId/AgentCollaborator.UserId
/// reference it, so it has to stay stable across restarts.</summary>
public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Username { get; set; }
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Editor;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set only when the appsettings.json entry used the plaintext "Password" field
    /// (dev convenience) instead of a real "PasswordHash". Compared directly in
    /// UserService.VerifyPasswordAsync instead of through PasswordHasher, since there's no hash
    /// to verify against.</summary>
    public string? ConfigPlaintextPassword { get; set; }
}

/// <summary>One user account defined in appsettings.json's "Users" array — the only source of
/// accounts (no database table). Set either Password (plaintext, dev convenience — never stored,
/// compared directly) or PasswordHash (a real ASP.NET Core PasswordHasher&lt;User&gt; hash, for
/// production); if both are set, Password wins.</summary>
public sealed class UserConfig
{
    public required string Username { get; set; }
    public string? Password { get; set; }
    public string? PasswordHash { get; set; }
    public UserRole Role { get; set; } = UserRole.Editor;
}
