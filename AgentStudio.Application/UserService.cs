using AgentStudio.Domain;
using Microsoft.AspNetCore.Identity;

namespace AgentStudio.Application;

/// <summary>User accounts are entirely config-defined (appsettings.json's "Users" array) — no
/// create/rename/delete/role-change here, that's done by editing the file and restarting.</summary>
public sealed class UserService
{
    private readonly IUserRepository _users;
    private readonly PasswordHasher<User> _hasher = new();

    public UserService(IUserRepository users) => _users = users;

    public Task<bool> AnyUsersAsync(CancellationToken ct = default) => _users.AnyAsync(ct);

    public Task<List<User>> ListAsync(CancellationToken ct = default) => _users.ListAsync(ct);

    public async Task<User?> VerifyPasswordAsync(string username, string password, CancellationToken ct = default)
    {
        var user = await _users.GetByUsernameAsync(username, ct);
        if (user is null) return null;

        // A config-sourced account with a plaintext "Password" in appsettings.json has no hash
        // to verify against — compare directly, using fixed-time comparison so a failed check
        // doesn't leak how many leading characters matched.
        if (user.ConfigPlaintextPassword is not null)
        {
            var expected = System.Text.Encoding.UTF8.GetBytes(user.ConfigPlaintextPassword);
            var actual = System.Text.Encoding.UTF8.GetBytes(password);
            return expected.Length == actual.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual)
                ? user
                : null;
        }

        return _hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed
            ? null
            : user;
    }
}
