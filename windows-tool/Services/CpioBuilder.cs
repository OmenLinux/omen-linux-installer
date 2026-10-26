using System.IO;
using System.Text;

namespace OmenPrep.Services;

/// <summary>
/// Builds a minimal "newc" format cpio archive - the same format mkinitcpio
/// writes initramfs with. Used to build a small standalone archive holding
/// patched ACPI table(s) under kernel/firmware/acpi/ plus omen-prep live
/// persist helpers, which gets PREPENDED to an existing initramfs.
///
/// Order matters: Documentation/admin-guide/acpi/initrd_table_override.rst
/// requires the uncompressed ACPI-override cpio to be first; the original
/// (typically compressed) initramfs is concatenated after it.
/// </summary>
public static class CpioBuilder
{
    private const string Magic = "070701";

    private static readonly int DirMode = Convert.ToInt32("40755", 8);
    private static readonly int FileMode = Convert.ToInt32("100644", 8);
    private static readonly int ExecMode = Convert.ToInt32("100755", 8);

    private static string Hex8(long v) => ((uint)v).ToString("x8");

    private static byte[] BuildEntry(string name, int mode, byte[] data, int ino)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var nameSize = nameBytes.Length + 1; // includes trailing NUL

        var header = new StringBuilder(110);
        header.Append(Magic);
        header.Append(Hex8(ino));
        header.Append(Hex8(mode));
        header.Append(Hex8(0)); // uid
        header.Append(Hex8(0)); // gid
        header.Append(Hex8(1)); // nlink
        header.Append(Hex8(0)); // mtime
        header.Append(Hex8(data.Length)); // filesize
        header.Append(Hex8(0)); // devmajor
        header.Append(Hex8(1)); // devminor
        header.Append(Hex8(0)); // rdevmajor
        header.Append(Hex8(0)); // rdevminor
        header.Append(Hex8(nameSize));
        header.Append(Hex8(0)); // checksum (unused for newc)

        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(header.ToString()));
        ms.Write(nameBytes);
        ms.WriteByte(0);
        PadTo4(ms);
        ms.Write(data);
        PadTo4(ms);
        return ms.ToArray();
    }

    private static void PadTo4(MemoryStream ms)
    {
        var rem = (int)(ms.Length % 4);
        if (rem == 0) return;
        for (var i = 0; i < 4 - rem; i++) ms.WriteByte(0);
    }

    private static void EnsureParentDirs(MemoryStream ms, HashSet<string> dirs, ref int ino, string filePath)
    {
        var parts = filePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var acc = "";
        for (var i = 0; i < parts.Length - 1; i++)
        {
            acc = i == 0 ? parts[0] : acc + "/" + parts[i];
            if (dirs.Add(acc))
                ms.Write(BuildEntry(acc, DirMode, [], ino++));
        }
    }

    /// <summary>
    /// Builds a cpio archive containing kernel/firmware/acpi/&lt;name&gt; for
    /// each entry in <paramref name="tables"/>, padded to a 512-byte boundary.
    /// </summary>
    public static byte[] BuildAcpiOverrideArchive(IReadOnlyDictionary<string, byte[]> tables) =>
        BuildEarlyInitramfsArchive(tables, extraFiles: null);

    /// <summary>
    /// ACPI override tables plus optional extra files (e.g. omen-prep persist
    /// scripts). Extra paths are POSIX paths without a leading slash.
    /// </summary>
    public static byte[] BuildEarlyInitramfsArchive(
        IReadOnlyDictionary<string, byte[]> tables,
        IReadOnlyDictionary<string, (byte[] Data, bool Executable)>? extraFiles)
    {
        using var ms = new MemoryStream();
        var ino = 1;
        var dirs = new HashSet<string>(StringComparer.Ordinal);

        void AddDir(string path)
        {
            if (dirs.Add(path))
                ms.Write(BuildEntry(path, DirMode, [], ino++));
        }

        AddDir("kernel");
        AddDir("kernel/firmware");
        AddDir("kernel/firmware/acpi");
        foreach (var (name, data) in tables)
        {
            ms.Write(BuildEntry($"kernel/firmware/acpi/{name}", FileMode, data, ino++));
        }

        if (extraFiles is not null)
        {
            foreach (var (path, (data, executable)) in extraFiles.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                EnsureParentDirs(ms, dirs, ref ino, path);
                ms.Write(BuildEntry(path, executable ? ExecMode : FileMode, data, ino++));
            }
        }

        ms.Write(BuildEntry("TRAILER!!!", 0, [], 0));

        var bytes = ms.ToArray();
        var padded = (bytes.Length + 511) / 512 * 512;
        if (padded == bytes.Length) return bytes;

        var result = new byte[padded];
        Array.Copy(bytes, result, bytes.Length);
        return result;
    }
}
