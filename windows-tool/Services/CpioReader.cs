using System.Text;

namespace OmenPrep.Services;

public sealed record CpioEntry(string Name, int Offset, byte[] Data);

/// <summary>
/// Scans a byte buffer for "newc" cpio entries, used to verify that
/// CpioBuilder's prepended ACPI-override archive actually landed inside an
/// initramfs - there's no visible "file" for this in a normal ISO/archive
/// browser (it's concatenated data inside an existing file, which generic
/// archive tools don't know to look past), so this is how omen-prep proves
/// it to the user instead of just asserting success.
///
/// Scans for the literal "070701" magic rather than assuming a fixed
/// offset, since early-microcode cpio (if any) and the original initramfs
/// may follow our archive and their layout varies.
/// </summary>
public static class CpioReader
{
    private static readonly byte[] Magic = "070701"u8.ToArray();

    public static List<CpioEntry> FindEntries(byte[] data, string? nameContains = null)
    {
        var results = new List<CpioEntry>();
        var pos = 0;

        while (true)
        {
            var found = IndexOf(data, Magic, pos);
            if (found < 0) break;

            var parsed = TryParseAt(data, found);
            if (parsed is not null)
            {
                var (entry, nextPos) = parsed.Value;
                if (nameContains is null || entry.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(entry);
                }
                pos = nextPos;
            }
            else
            {
                pos = found + 1;
            }
        }

        return results;
    }

    private static (CpioEntry entry, int nextPos)? TryParseAt(byte[] data, int start)
    {
        const int headerSize = 110;
        if (start + headerSize > data.Length) return null;

        try
        {
            var header = Encoding.ASCII.GetString(data, start, headerSize);
            if (header[..6] != "070701") return null;

            int Field(int offset) => Convert.ToInt32(header.Substring(offset, 8), 16);

            var filesize = Field(54);
            var namesize = Field(94);
            if (namesize is <= 0 or > 4096 || filesize < 0) return null;

            var namePos = start + headerSize;
            if (namePos + namesize > data.Length) return null;

            var name = Encoding.ASCII.GetString(data, namePos, namesize - 1);
            if (!IsPrintablePath(name)) return null;

            // newc pads relative to the archive/header start, not the absolute
            // buffer index. When an archive does not start at offset 0 mod 4
            // (e.g. scanning past leading junk in tests), Align4(absolutePos)
            // would mis-frame every subsequent field.
            var dataPos = start + Align4(headerSize + namesize);
            if (dataPos + filesize > data.Length) return null;

            var entryData = new byte[filesize];
            Array.Copy(data, dataPos, entryData, 0, filesize);

            var end = start + Align4(dataPos - start + filesize);
            return (new CpioEntry(name, start, entryData), end);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPrintablePath(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
        {
            if (c < 0x20 || c > 0x7E) return false;
        }
        return true;
    }

    private static int Align4(int pos) => pos % 4 == 0 ? pos : pos + (4 - pos % 4);

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}
