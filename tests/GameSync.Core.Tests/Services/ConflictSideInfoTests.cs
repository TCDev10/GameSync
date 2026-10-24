using FluentAssertions;
using GameSync.Core.Services;

namespace GameSync.Core.Tests.Services;

public sealed class ConflictSideInfoTests
{
    [Fact]
    public void Probe_MissingPath_ReturnsEmpty()
    {
        var result = ConflictSideInfo.Probe(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        result.Exists.Should().BeFalse();
        result.LastWriteUtc.Should().BeNull();
        result.SizeBytes.Should().BeNull();
    }

    [Fact]
    public void Probe_File_ReturnsWriteTimeAndSize()
    {
        var path = Path.Combine(Path.GetTempPath(), "gamesSync-conflict-" + Guid.NewGuid().ToString("N") + ".sav");
        try
        {
            File.WriteAllBytes(path, new byte[2048]);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc));

            var result = ConflictSideInfo.Probe(path);

            result.Exists.Should().BeTrue();
            result.SizeBytes.Should().Be(2048);
            result.LastWriteUtc.Should().NotBeNull();
            result.LastWriteUtc!.Value.UtcDateTime.Should().Be(new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void FormatSize_UsesHumanUnits()
    {
        ConflictSideInfo.FormatSize(512).Should().Be("512 B");
        ConflictSideInfo.FormatSize(2048).Should().Be("2 KB");
        ConflictSideInfo.FormatSize(null).Should().Be("Unknown size");
    }

    [Fact]
    public void FormatTimestamp_Null_IsUnknown()
    {
        ConflictSideInfo.FormatTimestamp(null).Should().Be("Unknown time");
    }
}
