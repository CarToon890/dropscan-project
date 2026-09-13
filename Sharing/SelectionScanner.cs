using System.IO;

namespace DropScan.Sharing;

public sealed record SelectionBatch(IReadOnlyList<SelectedFile> Entries, int Skipped);

public static class SelectionScanner
{
    public static bool HasReparsePoint(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    public static SelectionBatch Scan(IEnumerable<string> paths, IEnumerable<SelectedFile> existing)
    {
        var added = new List<SelectedFile>();
        var seen = existing.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rootNames = existing.Select(f => f.ArchivePath.Split('/')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        foreach (var raw in paths)
        {
            try
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
                if (HasReparsePoint(root)) { skipped++; continue; }
                if (Directory.Exists(root))
                {
                    var rootName = UniqueName(Path.GetFileName(root) is { Length: > 0 } name ? name : "Folder", rootNames);
                    var pending = new Stack<(string Path, string Relative)>();
                    pending.Push((root, rootName));
                    while (pending.TryPop(out var folder))
                    {
                        try
                        {
                            if (HasReparsePoint(folder.Path)) { skipped++; continue; }
                            var children = Directory.GetFileSystemEntries(folder.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
                            if (children.Length == 0 && seen.Add(folder.Path)) added.Add(new(folder.Path, Path.GetFileName(folder.Path), 0) { ArchivePath = folder.Relative + "/", IsDirectory = true });
                            foreach (var child in children)
                            {
                                try
                                {
                                    if (HasReparsePoint(child)) { skipped++; continue; }
                                    var relative = folder.Relative + "/" + Path.GetFileName(child);
                                    if (Directory.Exists(child)) pending.Push((child, relative));
                                    else AddFile(child, relative);
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                    }
                }
                else AddFile(root, Path.GetFileName(root));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { skipped++; }
        }
        return new(added, skipped);

        void AddFile(string path, string relative)
        {
            if (!seen.Add(path)) return;
            var info = new FileInfo(path);
            added.Add(new(path, info.Name, info.Length) { ArchivePath = relative });
        }
    }
    internal static string UniqueName(string name, HashSet<string> used)
    {
        var result = name;
        for (int suffix = 2; !used.Add(result); suffix++) result = $"{Path.GetFileNameWithoutExtension(name)} ({suffix}){Path.GetExtension(name)}";
        return result;
    }
}
