using FluentAssertions;
using GameSync.Core.Options;

namespace GameSync.Core.Tests.Options;

public sealed class GameSyncOptionsTests
{
    [Fact]
    public void NormalizeUpdateFeed_LegacyTcDevOwner_IsCorrectedToTcDev10()
    {
        var options = new GameSyncOptions
        {
            UpdateReleasesOwner = "TCDev",
            UpdateReleasesRepo = GameSyncOptions.DefaultUpdateReleasesRepo
        };

        options.NormalizeUpdateFeed();

        options.UpdateReleasesOwner.Should().Be(GameSyncOptions.DefaultUpdateReleasesOwner);
        options.UpdateReleasesOwner.Should().Be("TCDev10");
    }

    [Fact]
    public void NormalizeUpdateFeed_LegacyTcDev69Owner_IsCorrectedToTcDev10()
    {
        var options = new GameSyncOptions
        {
            UpdateReleasesOwner = "TCDev69",
            UpdateReleasesRepo = GameSyncOptions.DefaultUpdateReleasesRepo
        };

        options.NormalizeUpdateFeed();

        options.UpdateReleasesOwner.Should().Be("TCDev10");
    }

    [Fact]
    public void NormalizeUpdateFeed_CustomFork_IsLeftUnchanged()
    {
        var options = new GameSyncOptions
        {
            UpdateReleasesOwner = "TCDev",
            UpdateReleasesRepo = "MyFork"
        };

        options.NormalizeUpdateFeed();

        options.UpdateReleasesOwner.Should().Be("TCDev");
    }

    [Fact]
    public void DefaultUpdateReleasesOwner_IsTcDev10()
    {
        GameSyncOptions.DefaultUpdateReleasesOwner.Should().Be("TCDev10");
        new GameSyncOptions().UpdateReleasesOwner.Should().Be("TCDev10");
    }
}
