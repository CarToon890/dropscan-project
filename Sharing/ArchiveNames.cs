using System.IO;

namespace DropScan.Sharing;

internal static class ArchiveNames
{
    public static IReadOnlyList<string> Allocate(IEnumerable<SelectedFile> entries)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var entry in entries)
        {
            string raw = entry.ArchivePath.Replace('\\', '/').TrimEnd('/');
            var parts = raw.Split('/');
            if (parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new IOException("ชื่อใน ZIP ไม่ถูกต้อง");
            string parent = "", original = "";
            int directoryCount = entry.IsDirectory ? parts.Length : parts.Length - 1;
            for (int i = 0; i < directoryCount; i++)
            {
                original += (original.Length == 0 ? "" : "/") + parts[i];
                if (!directories.TryGetValue(original, out var allocated))
                {
                    allocated = Unique(parent, parts[i], used);
                    directories.Add(original, allocated);
                }
                parent = allocated;
            }
            result.Add(entry.IsDirectory ? parent + "/" : Unique(parent, parts[^1], used));
        }
        return result;
    }
    static string Unique(string parent, string name, HashSet<string> used)
    {
        string prefix = parent.Length == 0 ? "" : parent + "/";
        string candidate = prefix + name;
        for (int n = 2; !used.Add(candidate); n++) candidate = prefix + Path.GetFileNameWithoutExtension(name) + $" ({n})" + Path.GetExtension(name);
        return candidate;
    }
}
