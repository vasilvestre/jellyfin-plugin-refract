using Jellyfin.Plugin.Refract;

namespace Jellyfin.Plugin.Refract.Tests;

/// <summary>
/// The movie idempotency key. Refract keeps keys for 24 hours and a repeated key replays the
/// first answer, so the key must be stable for one viewing and different for the next one.
/// </summary>
public class IdempotencyKeyTests
{
    private static readonly Guid User = Guid.Parse("8e5feed4-2893-434a-b0c4-ed8edf15de49");
    private static readonly Guid Movie = Guid.Parse("4df4c1c2-b469-4ab9-af13-d13838f71150");
    private static readonly DateTime Watched = new(2026, 11, 13, 21, 40, 7, DateTimeKind.Utc);

    /// <summary>A retry of the same save must reuse the key, or Refract records two plays.</summary>
    [Fact]
    public void TheSameViewingGivesTheSameKey() =>
        Assert.Equal(RefractMediator.IdempotencyKey(User, Movie, Watched), RefractMediator.IdempotencyKey(User, Movie, Watched));

    /// <summary>Two users watching the same film are two plays on two accounts.</summary>
    [Fact]
    public void AnotherUserGivesAnotherKey() =>
        Assert.NotEqual(RefractMediator.IdempotencyKey(User, Movie, Watched), RefractMediator.IdempotencyKey(Guid.NewGuid(), Movie, Watched));

    /// <summary>Two films are two plays.</summary>
    [Fact]
    public void AnotherMovieGivesAnotherKey() =>
        Assert.NotEqual(RefractMediator.IdempotencyKey(User, Movie, Watched), RefractMediator.IdempotencyKey(User, Guid.NewGuid(), Watched));

    /// <summary>Watching the same film again later is a rewatch, not a replay.</summary>
    [Fact]
    public void AnotherMinuteGivesAnotherKey() =>
        Assert.NotEqual(RefractMediator.IdempotencyKey(User, Movie, Watched), RefractMediator.IdempotencyKey(User, Movie, Watched.AddMinutes(1)));

    /// <summary>The key is a 36 character GUID, which is what Refract echoes back.</summary>
    [Fact]
    public void TheKeyIsAGuid() => Assert.True(Guid.TryParse(RefractMediator.IdempotencyKey(User, Movie, Watched), out _));
}
