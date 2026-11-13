using Jellyfin.Plugin.Refract.Configuration;
using Jellyfin.Plugin.Refract.Model;

namespace Jellyfin.Plugin.Refract.Tests;

/// <summary>
/// The credential store. Losing or duplicating an entry logs plays on the wrong account.
/// </summary>
public class ConfigurationTests
{
    /// <summary>Approving twice for the same user must leave one entry, the newest.</summary>
    [Fact]
    public void PutUserReplacesTheEntryOfTheSameUser()
    {
        var userId = Guid.NewGuid();
        var config = new PluginConfiguration();

        config.PutUser(new RefractUser { LinkedMbUserId = userId, RefreshToken = "first" });
        config.PutUser(new RefractUser { LinkedMbUserId = userId, RefreshToken = "second" });

        Assert.Single(config.RefractUsers);
        Assert.Equal("second", config.GetUser(userId)?.RefreshToken);
    }

    /// <summary>Unlinking one user must not touch the others.</summary>
    [Fact]
    public void RemoveUserKeepsTheOtherUsers()
    {
        var kept = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var config = new PluginConfiguration();
        config.PutUser(new RefractUser { LinkedMbUserId = kept });
        config.PutUser(new RefractUser { LinkedMbUserId = removed });

        config.RemoveUser(removed);

        Assert.NotNull(config.GetUser(kept));
        Assert.Null(config.GetUser(removed));
    }

    /// <summary>An unlinked user has no credential, and the mediator must not send anything.</summary>
    [Fact]
    public void GetUserReturnsNullForAnUnknownUser() =>
        Assert.Null(new PluginConfiguration().GetUser(Guid.NewGuid()));

    /// <summary>Servers where only a few members use Refract start with the button on.</summary>
    [Fact]
    public void TheLinkButtonIsShownByDefault() =>
        Assert.True(new PluginConfiguration().ShowLinkButton);

    /// <summary>A user counts as linked once a refresh token is stored.</summary>
    /// <param name="refreshToken">The stored refresh token.</param>
    /// <param name="expected">Whether the user counts as linked.</param>
    [Theory]
    [InlineData("", false)]
    [InlineData("rfrt_abc", true)]
    public void IsLinkedFollowsTheRefreshToken(string refreshToken, bool expected) =>
        Assert.Equal(expected, new RefractUser { RefreshToken = refreshToken }.IsLinked);
}
