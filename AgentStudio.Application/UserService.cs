using AgentStudio.Domain;
using Microsoft.AspNetCore.Identity;

namespace AgentStudio.Application;

public sealed class UserService
{
    private readonly IUserRepository _users;
    private readonly PasswordHasher<User> _hasher = new();

    public UserService(IUserRepository users) => _users = users;

    public Task<bool> AnyUsersAsync(CancellationToken ct = default) => _users.AnyAsync(ct);

    public Task<List<User>> ListAsync(CancellationToken ct = default) => _users.ListAsync(ct);

    public async Task<User> CreateAsync(string username, string password, UserRole role, CancellationToken ct = default)
    {
        if (await _users.GetByUsernameAsync(username, ct) is not null)
            throw new InvalidOperationException($"Username '{username}' is already taken.");

        var user = new User { Username = username, Role = role };
        user.PasswordHash = _hasher.HashPassword(user, password);
        await _users.AddAsync(user, ct);
        await _users.SaveChangesAsync(ct);
        return user;
    }

    public async Task<User?> VerifyPasswordAsync(string username, string password, CancellationToken ct = default)
    {
        var user = await _users.GetByUsernameAsync(username, ct);
        if (user is null) return null;
        return _hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed
            ? null
            : user;
    }

    public async Task ChangeRoleAsync(Guid userId, UserRole role, CancellationToken ct = default)
    {
        var user = await _users.GetAsync(userId, ct) ?? throw new KeyNotFoundException("User not found.");
        if (user.Role == UserRole.Admin && role != UserRole.Admin)
        {
            var admins = (await _users.ListAsync(ct)).Count(u => u.Role == UserRole.Admin);
            if (admins <= 1)
                throw new InvalidOperationException("Cannot demote the last admin account.");
        }
        user.Role = role;
        await _users.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetAsync(userId, ct) ?? throw new KeyNotFoundException("User not found.");
        if (user.Role == UserRole.Admin)
        {
            var admins = (await _users.ListAsync(ct)).Count(u => u.Role == UserRole.Admin);
            if (admins <= 1)
                throw new InvalidOperationException("Cannot delete the last admin account.");
        }
        await _users.DeleteAsync(user, ct);
        await _users.SaveChangesAsync(ct);
    }
}
