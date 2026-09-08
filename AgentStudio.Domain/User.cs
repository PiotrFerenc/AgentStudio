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
}
