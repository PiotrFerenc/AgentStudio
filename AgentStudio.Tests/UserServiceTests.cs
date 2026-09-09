using AgentStudio.Application;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

public class UserServiceTests
{
    private static AgentStudioDbContext NewDb(string dbName) =>
        new(new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(dbName).Options);

    private static UserService NewService(string dbName) =>
        new(new UserRepository(NewDb(dbName)));

    private static UserService NewServiceWithConfig(string dbName, params UserConfig[] configUsers) =>
        new(new UserRepository(NewDb(dbName), Options.Create(configUsers.ToList())));

    [Fact]
    public async Task CreateAsync_hashes_password_and_verifies_correctly()
    {
        var service = NewService("users-create");
        var user = await service.CreateAsync("alice", "correct-horse-battery", UserRole.Editor);

        Assert.NotEqual("correct-horse-battery", user.PasswordHash);
        Assert.NotNull(await service.VerifyPasswordAsync("alice", "correct-horse-battery"));
        Assert.Null(await service.VerifyPasswordAsync("alice", "wrong-password"));
    }

    [Fact]
    public async Task CreateAsync_duplicate_username_throws()
    {
        var service = NewService("users-dup");
        await service.CreateAsync("bob", "password123", UserRole.Editor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("bob", "other-password", UserRole.Admin));
    }

    [Fact]
    public async Task VerifyPasswordAsync_unknown_username_returns_null()
    {
        var service = NewService("users-unknown");
        Assert.Null(await service.VerifyPasswordAsync("nobody", "whatever", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_last_admin_throws()
    {
        var service = NewService("users-delete-last-admin");
        var admin = await service.CreateAsync("admin", "password123", UserRole.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(admin.Id));
    }

    [Fact]
    public async Task DeleteAsync_non_last_admin_succeeds()
    {
        var service = NewService("users-delete-second-admin");
        await service.CreateAsync("admin1", "password123", UserRole.Admin);
        var admin2 = await service.CreateAsync("admin2", "password123", UserRole.Admin);

        await service.DeleteAsync(admin2.Id);

        Assert.DoesNotContain(await service.ListAsync(), u => u.Id == admin2.Id);
    }

    [Fact]
    public async Task ChangeRoleAsync_demoting_last_admin_throws()
    {
        var service = NewService("users-demote-last-admin");
        var admin = await service.CreateAsync("admin", "password123", UserRole.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ChangeRoleAsync(admin.Id, UserRole.Editor));
    }

    [Fact]
    public async Task ChangeRoleAsync_demoting_one_of_several_admins_succeeds()
    {
        var service = NewService("users-demote-extra-admin");
        await service.CreateAsync("admin1", "password123", UserRole.Admin);
        var admin2 = await service.CreateAsync("admin2", "password123", UserRole.Admin);

        await service.ChangeRoleAsync(admin2.Id, UserRole.Editor);

        var users = await service.ListAsync();
        Assert.Equal(UserRole.Editor, users.Single(u => u.Id == admin2.Id).Role);
    }

    [Fact]
    public async Task VerifyPasswordAsync_config_user_with_plaintext_password_verifies_correctly()
    {
        var service = NewServiceWithConfig("users-config-plain",
            new UserConfig { Username = "cfg-alice", Password = "dev-password", Role = UserRole.Admin });

        Assert.NotNull(await service.VerifyPasswordAsync("cfg-alice", "dev-password"));
        Assert.Null(await service.VerifyPasswordAsync("cfg-alice", "wrong-password"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_config_user_with_password_hash_verifies_correctly()
    {
        var hasher = new PasswordHasher<User>();
        var hash = hasher.HashPassword(new User { Username = "cfg-bob" }, "prod-password");
        var service = NewServiceWithConfig("users-config-hash",
            new UserConfig { Username = "cfg-bob", PasswordHash = hash, Role = UserRole.Editor });

        Assert.NotNull(await service.VerifyPasswordAsync("cfg-bob", "prod-password"));
        Assert.Null(await service.VerifyPasswordAsync("cfg-bob", "wrong-password"));
    }

    [Fact]
    public async Task ListAsync_config_user_wins_over_database_user_with_same_username()
    {
        var db = NewDb("users-config-wins");
        var repo = new UserRepository(db, Options.Create(new List<UserConfig>
        {
            new() { Username = "shared", Password = "cfg-pass", Role = UserRole.Admin }
        }));
        var service = new UserService(repo);
        // A DB row with the same username, created directly (bypassing the collision check in
        // CreateAsync) to prove ListAsync/GetByUsernameAsync themselves prefer config, not just
        // that CreateAsync refuses the collision.
        db.Users.Add(new User { Username = "shared", Role = UserRole.Editor, PasswordHash = "irrelevant" });
        await db.SaveChangesAsync();

        var list = await service.ListAsync();
        var shared = Assert.Single(list, u => u.Username == "shared");
        Assert.Equal(UserRole.Admin, shared.Role);
        Assert.True(shared.IsFromConfig);
    }

    [Fact]
    public async Task ChangeRoleAsync_on_config_user_throws()
    {
        var service = NewServiceWithConfig("users-config-changerole",
            new UserConfig { Username = "cfg-carol", Password = "x", Role = UserRole.Editor });
        var user = (await service.ListAsync()).Single();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ChangeRoleAsync(user.Id, UserRole.Admin));
        Assert.Contains("appsettings.json", ex.Message);
    }

    [Fact]
    public async Task DeleteAsync_on_config_user_throws()
    {
        var service = NewServiceWithConfig("users-config-delete",
            new UserConfig { Username = "cfg-dave", Password = "x", Role = UserRole.Editor });
        var user = (await service.ListAsync()).Single();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(user.Id));
        Assert.Contains("appsettings.json", ex.Message);
    }

    [Fact]
    public async Task Config_user_gets_a_stable_deterministic_id_across_repository_instances()
    {
        var db = NewDb("users-config-stable-id");
        var cfg = new UserConfig { Username = "stable", Password = "x", Role = UserRole.Editor };
        var id1 = (await new UserRepository(db, Options.Create(new List<UserConfig> { cfg })).GetByUsernameAsync("stable"))!.Id;
        var id2 = (await new UserRepository(db, Options.Create(new List<UserConfig> { cfg })).GetByUsernameAsync("stable"))!.Id;

        Assert.Equal(id1, id2);
    }

    [Fact]
    public async Task AnyUsersAsync_true_when_only_config_defines_users()
    {
        var service = NewServiceWithConfig("users-config-any",
            new UserConfig { Username = "cfg-erin", Password = "x", Role = UserRole.Admin });

        Assert.True(await service.AnyUsersAsync());
    }
}
