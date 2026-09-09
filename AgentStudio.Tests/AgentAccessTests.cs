using AgentStudio.Domain;
using Xunit;

namespace AgentStudio.Tests;

public class AgentAccessTests
{
    private static Agent AgentOwnedBy(Guid? ownerId) => new() { Name = "t", OwnerId = ownerId };
    private static User UserWithRole(UserRole role, Guid? id = null) => new() { Id = id ?? Guid.NewGuid(), Username = "u", Role = role };

    [Fact]
    public void Admin_can_edit_any_agent()
    {
        var agent = AgentOwnedBy(Guid.NewGuid());
        var admin = UserWithRole(UserRole.Admin);

        Assert.True(AgentAccess.CanEdit(agent, admin));
    }

    [Fact]
    public void Owner_can_edit_their_own_agent()
    {
        var owner = UserWithRole(UserRole.Editor);
        var agent = AgentOwnedBy(owner.Id);

        Assert.True(AgentAccess.CanEdit(agent, owner));
    }

    [Fact]
    public void Listed_collaborator_can_edit()
    {
        var user = UserWithRole(UserRole.Editor);
        var agent = AgentOwnedBy(Guid.NewGuid());
        agent.Collaborators.Add(new AgentCollaborator { AgentId = agent.Id, UserId = user.Id, Username = user.Username });

        Assert.True(AgentAccess.CanEdit(agent, user));
    }

    [Fact]
    public void Unrelated_editor_cannot_edit()
    {
        var agent = AgentOwnedBy(Guid.NewGuid());
        var stranger = UserWithRole(UserRole.Editor);

        Assert.False(AgentAccess.CanEdit(agent, stranger));
    }

    [Fact]
    public void Agent_with_no_owner_and_no_collaborators_is_editable_only_by_admin()
    {
        var agent = AgentOwnedBy(null);
        var editor = UserWithRole(UserRole.Editor);
        var admin = UserWithRole(UserRole.Admin);

        Assert.False(AgentAccess.CanEdit(agent, editor));
        Assert.True(AgentAccess.CanEdit(agent, admin));
    }
}
