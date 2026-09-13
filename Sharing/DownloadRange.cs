using System.Globalization;

namespace DropScan.Sharing;

public sealed record DownloadRange(long Start, long Length, bool Partial)
{
    public static DownloadRange? Parse(string? value, long length)
    {
        if (string.IsNullOrEmpty(value)) return new(0, length, false);
        // Ignore unsupported units, malformed syntax and multipart requests; serve a full representation.
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || value.Contains(',')) return new(0, length, false);
        var parts = value[6..].Trim().Split('-');
        if (parts.Length != 2) return new(0, length, false);
        static bool Number(string s, out long n) => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
        if (parts[0].Length == 0)
        {
            if (!Number(parts[1], out var suffix)) return new(0, length, false);
            if (suffix == 0 || length == 0) return null;
            var count = Math.Min(suffix, length);
            return new(length - count, count, true);
        }
        if (!Number(parts[0], out var start)) return new(0, length, false);
        long end = length - 1;
        if (parts[1].Length > 0 && !Number(parts[1], out end)) return new(0, length, false);
        if (start >= length || end < start || length == 0) return null;
        end = Math.Min(end, length - 1);
        return new(start, end - start + 1, true);
    }
}
