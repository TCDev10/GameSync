namespace GameSync.Core.Services;

/// <summary>
/// Filesystem probe used to fill Steam-style conflict dialog metadata.
/// </summary>
public static class ConflictSideInfo
{
    public readonly record struct ProbeResult(bool Exists, DateTimeOffset? LastWriteUtc, long? SizeBytes);

    public static ProbeResult Probe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return default;
        }

        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                return new ProbeResult(true, info.LastWriteTimeUtc, info.Length);
            }

            if (Directory.Exists(path))
            {
                DateTimeOffset? newest = null;
                long total = 0;
                var any = false;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    any = true;
                    var info = new FileInfo(file);
                    total += info.Length;
                    var write = info.LastWriteTimeUtc;
                    if (newest is null || write > newest)
                    {
                        newest = write;
                    }
                }

                if (!any)
                {
                    var dirWrite = Directory.GetLastWriteTimeUtc(path);
                    return new ProbeResult(true, dirWrite, 0);
                }

                return new ProbeResult(true, newest, total);
            }
        }
        catch
        {
            // Missing permissions / race: treat as unknown.
        }

        return default;
    }

    public static string FormatTimestamp(DateTimeOffset? utc)
    {
        if (utc is null)
        {
            return "Unknown time";
        }

        var local = utc.Value.ToLocalTime();
        return local.ToString("g");
    }

    public static string FormatSize(long? bytes)
    {
        if (bytes is null)
        {
            return "Unknown size";
        }

        if (bytes.Value < 1024)
        {
            return $"{bytes.Value} B";
        }

        double value = bytes.Value;
        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = 0;
        value /= 1024d;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    public static string DescribeExistence(bool exists, DateTimeOffset? write, long? size)
    {
        if (!exists && write is null && size is null)
        {
            return "Not found";
        }

        if (!exists)
        {
            return "Missing on this side";
        }

        return $"{FormatTimestamp(write)} · {FormatSize(size)}";
    }
}
