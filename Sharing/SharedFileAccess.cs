using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DropScan.Sharing;

public static class SharedFileAccess
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    public static FileStream Open(SharedFile file)
    {
        if (file.IsDirectory || SelectionScanner.HasReparsePoint(file.Path)) throw new IOException("รายการไฟล์เปลี่ยนไป");
        var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var resolved = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(stream.SafeFileHandle, resolved, (uint)resolved.Capacity, 0);
            var expected = Path.GetFullPath(file.Path);
            var actual = resolved.ToString();
            if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
            else if (actual.StartsWith(@"\\?\")) actual = actual[4..];
            if (length == 0 || length >= resolved.Capacity || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) || stream.Length != file.Length || File.GetLastWriteTimeUtc(file.Path) != file.LastWriteUtc)
                throw new IOException("ไฟล์เปลี่ยนไปจากรายการที่แชร์");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
}
