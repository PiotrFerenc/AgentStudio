using AgentStudio.Application;
using AgentStudio.Domain;
using AgentStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentStudio.Tests;

public class UserServiceTests
{
    private static UserService NewService(string dbName) =>
        new(new UserRepository(new AgentStudioDbContext(
            new DbContextOptionsBuilder<AgentStudioDbContext>().UseInMemoryDatabase(dbName).Options)));

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
}
