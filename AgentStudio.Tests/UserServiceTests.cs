using AgentStudio.Application;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

public class UserServiceTests
{
    private static UserService NewService(params UserConfig[] configUsers) =>
        new(new UserRepository(Options.Create(configUsers.ToList())));

    [Fact]
    public async Task VerifyPasswordAsync_plaintext_password_verifies_correctly()
    {
        var service = NewService(new UserConfig { Username = "alice", Password = "correct-horse-battery", Role = UserRole.Editor });

        Assert.NotNull(await service.VerifyPasswordAsync("alice", "correct-horse-battery"));
        Assert.Null(await service.VerifyPasswordAsync("alice", "wrong-password"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_password_hash_verifies_correctly()
    {
        var hasher = new PasswordHasher<User>();
        var hash = hasher.HashPassword(new User { Username = "bob" }, "prod-password");
        var service = NewService(new UserConfig { Username = "bob", PasswordHash = hash, Role = UserRole.Admin });

        Assert.NotNull(await service.VerifyPasswordAsync("bob", "prod-password"));
        Assert.Null(await service.VerifyPasswordAsync("bob", "wrong-password"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_unknown_username_returns_null()
    {
        var service = NewService();
        Assert.Null(await service.VerifyPasswordAsync("nobody", "whatever", CancellationToken.None));
    }

    [Fact]
    public async Task ListAsync_returns_every_configured_user()
    {
        var service = NewService(
            new UserConfig { Username = "carol", Password = "x", Role = UserRole.Admin },
            new UserConfig { Username = "dave", Password = "y", Role = UserRole.Editor });

        var users = await service.ListAsync();

        Assert.Contains(users, u => u.Username == "carol" && u.Role == UserRole.Admin);
        Assert.Contains(users, u => u.Username == "dave" && u.Role == UserRole.Editor);
    }

    [Fact]
    public async Task AnyUsersAsync_true_when_config_defines_users()
    {
        var service = NewService(new UserConfig { Username = "erin", Password = "x", Role = UserRole.Admin });
        Assert.True(await service.AnyUsersAsync());
    }

    [Fact]
    public async Task AnyUsersAsync_false_when_no_users_configured()
    {
        var service = NewService();
        Assert.False(await service.AnyUsersAsync());
    }

    [Fact]
    public async Task User_gets_a_stable_deterministic_id_across_repository_instances()
    {
        var id1 = (await NewService(new UserConfig { Username = "stable", Password = "x", Role = UserRole.Editor })
            .ListAsync()).Single().Id;
        var id2 = (await NewService(new UserConfig { Username = "stable", Password = "x", Role = UserRole.Editor })
            .ListAsync()).Single().Id;

        Assert.Equal(id1, id2);
    }
}
