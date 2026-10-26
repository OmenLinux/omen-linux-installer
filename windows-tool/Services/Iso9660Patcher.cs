using System.IO;
using System.Text;

namespace OmenPrep.Services;

/// <summary>
/// Finds and, if needed, rewrites a single file's directory-record pointer
/// inside an existing ISO9660 image, without touching anything else - no
/// full rebuild, no El Torito/boot-catalog involvement at all. This is
/// deliberately a minimal, surgical operation: DiscUtils' Iso9660 builder
/// only models a single El Torito boot entry, but real hybrid Linux
/// install ISOs have two (BIOS + UEFI); rebuilding through it risks
/// silently dropping one. Growing one file in place isn't possible
/// (ISO9660 extents are packed tight), so instead: write the new, larger
/// version of the file's data past the end of the image, then overwrite
/// just that one directory record's 16-byte (extent LBA + data length)
/// pointer to point at the new location. Every other byte of the original
/// image - boot catalog, bootloaders, every other file - is untouched.
///
/// After any grow we also (1) bump ISO9660 Volume Space Size so the new
/// extents are inside the volume, and (2) relocate the protective GPT
/// backup header to the new end of image. Skipping those leaves hybrid
/// Ubuntu/Fedora USBs with a GPT whose Alternate LBA is mid-disk — many
/// firmwares then hide the EFI boot entry entirely.
///
/// Based on ECMA-119 (ISO9660) directory record layout and the
/// Rock Ridge / SUSP "NM" extension for long POSIX names.
/// </summary>
public static class Iso9660Patcher
{
    private const int SectorSize = 2048;
    private const int GptSectorSize = 512;

    public sealed record LocatedFile(string TreeName, long RecordOffset, uint Lba, uint DataLength);

    private sealed record DirRecord(
        long AbsoluteOffset,
        int LenDr,
        uint ExtentLba,
        uint DataLength,
        bool IsDirectory,
        string Name);

    /// <summary>Finds a file by POSIX-style path (e.g. "/casper/initrd") in every volume tree (Primary + Joliet, if present) that has it.</summary>
    public static List<LocatedFile> LocateFile(Stream iso, string isoPath)
    {
        var components = isoPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<LocatedFile>();

        foreach (var tree in FindVolumeTrees(iso))
        {
            var current = tree.Root;
            DirRecord? match = current;
            foreach (var component in components)
            {
                var entries = ReadDirectory(iso, current, tree.IsJoliet);
                var next = entries.FirstOrDefault(e =>
                    !string.Equals(e.Name, ".", StringComparison.Ordinal) &&
                    !string.Equals(e.Name, "..", StringComparison.Ordinal) &&
                    string.Equals(StripVersion(e.Name), component, StringComparison.OrdinalIgnoreCase));
                if (next is null) { match = null; break; }
                current = next;
                match = next;
            }

            if (match is not null && !ReferenceEquals(match, tree.Root))
            {
                results.Add(new LocatedFile(tree.Name, match.AbsoluteOffset, match.ExtentLba, match.DataLength));
            }
        }

        return results;
    }

    /// <summary>
    /// Prepends <paramref name="prefixBytes"/> to the file found at
    /// <paramref name="isoPath"/> and writes the result to
    /// <paramref name="destIsoPath"/>, patching the located directory
    /// record(s) (Primary and Joliet, if present) to point at the new,
    /// relocated copy. The original bytes at the old location are left in
    /// place (now unreferenced) rather than reclaimed, to avoid touching
    /// anything else in the image.
    ///
    /// Prepend (not append) is required for ACPI table overrides: the
    /// kernel only loads <c>kernel/firmware/acpi/*.aml</c> from an
    /// uncompressed cpio that is the *first* archive in the initramfs
    /// (see Documentation/admin-guide/acpi/initrd_table_override.rst).
    /// Trailing data after a compressed initramfs is often ignored.
    /// </summary>
    public static async Task InjectPrependedFileAsync(
        string sourceIsoPath, string destIsoPath, string isoPath, byte[] prefixBytes)
    {
        List<LocatedFile> located;
        long originalTotalLength;
        byte[] originalFileBytes;

        using (var source = new FileStream(sourceIsoPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            located = LocateFile(source, isoPath);
            if (located.Count == 0)
            {
                throw new FileNotFoundException($"'{isoPath}' not found in any volume tree of this ISO.");
            }

            originalTotalLength = source.Length;
            var primary = located[0];
            originalFileBytes = new byte[primary.DataLength];
            source.Seek((long)primary.Lba * SectorSize, SeekOrigin.Begin);
            await source.ReadExactlyAsync(originalFileBytes, 0, originalFileBytes.Length);
        }

        if (originalTotalLength % SectorSize != 0)
        {
            throw new InvalidDataException("ISO size is not a whole number of sectors - refusing to patch.");
        }

        var newLba = (uint)(originalTotalLength / SectorSize);
        var newData = new byte[prefixBytes.Length + originalFileBytes.Length];
        Buffer.BlockCopy(prefixBytes, 0, newData, 0, prefixBytes.Length);
        Buffer.BlockCopy(originalFileBytes, 0, newData, prefixBytes.Length, originalFileBytes.Length);
        var newLength = (uint)newData.Length;

        var paddedLength = ((newData.Length + SectorSize - 1) / SectorSize) * SectorSize;

        // Stream-copy the original image verbatim, then append the relocated file.
        using (var src = new FileStream(sourceIsoPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var dst = new FileStream(destIsoPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            await src.CopyToAsync(dst);
            await dst.WriteAsync(newData, 0, newData.Length);
            if (paddedLength != newData.Length)
            {
                await dst.WriteAsync(new byte[paddedLength - newData.Length]);
            }

            foreach (var loc in located)
            {
                PatchDirectoryRecord(dst, loc.RecordOffset, newLba, newLength);
            }

            FinalizeIsoGrowth(dst);
            await dst.FlushAsync();
        }
    }

    /// <summary>
    /// Replaces the entire contents of an existing file, relocating its
    /// data extent past the end of the image and patching Primary/Joliet
    /// directory records. Used to grow bootloader configs when injecting
    /// the <c>init=/omen-prep/...</c> cmdline.
    /// </summary>
    public static async Task ReplaceFileAsync(string isoPath, string fileIsoPath, byte[] newContent)
    {
        List<LocatedFile> located;
        long originalTotalLength;

        using (var source = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            located = LocateFile(source, fileIsoPath);
            if (located.Count == 0)
                throw new FileNotFoundException($"'{fileIsoPath}' not found in any volume tree of this ISO.");
            originalTotalLength = source.Length;
        }

        if (originalTotalLength % SectorSize != 0)
            throw new InvalidDataException("ISO size is not a whole number of sectors - refusing to patch.");

        // Work in-place: append new payload, then patch directory records.
        var newLba = (uint)(originalTotalLength / SectorSize);
        var paddedLength = ((newContent.Length + SectorSize - 1) / SectorSize) * SectorSize;

        using (var iso = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            iso.Seek(0, SeekOrigin.End);
            await iso.WriteAsync(newContent);
            if (paddedLength != newContent.Length)
                await iso.WriteAsync(new byte[paddedLength - newContent.Length]);

            foreach (var loc in located)
                PatchDirectoryRecord(iso, loc.RecordOffset, newLba, (uint)newContent.Length);

            FinalizeIsoGrowth(iso);
            await iso.FlushAsync();
        }
    }

    /// <summary>
    /// Adds a new file at the ISO root (Primary + Joliet if present) without
    /// rebuilding the image. File data and a relocated root directory are
    /// appended past the end of the image; first-level subdirs' <c>..</c>
    /// pointers are patched to the new root. Used to ship the post-install
    /// keyboard-driver script alongside the ACPI fix.
    /// </summary>
    public static async Task AddRootFileAsync(
        string isoPath, string iso9660FileName, string jolietFileName, byte[] content, string? rockRidgeName = null)
    {
        if (content.Length == 0) throw new ArgumentException("File content must not be empty.", nameof(content));

        using var iso = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (iso.Length % SectorSize != 0)
            throw new InvalidDataException("ISO size is not a whole number of sectors - refusing to patch.");

        var trees = FindVolumeTrees(iso);
        if (trees.Count == 0)
            throw new InvalidDataException("No ISO9660 Primary/Joliet volume descriptor found.");

        // Append file payload once; both trees point at the same data extent.
        var fileLba = (uint)(iso.Length / SectorSize);
        var filePadded = ((content.Length + SectorSize - 1) / SectorSize) * SectorSize;
        iso.Seek(0, SeekOrigin.End);
        await iso.WriteAsync(content);
        if (filePadded != content.Length)
            await iso.WriteAsync(new byte[filePadded - content.Length]);

        foreach (var tree in trees)
        {
            var useJoliet = tree.IsJoliet;
            var displayName = useJoliet ? jolietFileName : iso9660FileName;
            var rrName = useJoliet ? null : rockRidgeName;

            var existing = ReadDirectory(iso, tree.Root, useJoliet);
            if (existing.Any(e => string.Equals(StripVersion(e.Name), StripVersion(displayName), StringComparison.OrdinalIgnoreCase) ||
                                  (rrName is not null && string.Equals(e.Name, rrName, StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidOperationException($"Root already contains '{displayName}'.");
            }

            var hasRockRidge = !useJoliet && existing.Any(e => e.LenDr > 34 && e.Name is not ("." or ".."));
            var newRecord = BuildFileDirectoryRecord(
                fileLba, (uint)content.Length, displayName, useJoliet, hasRockRidge ? rrName : null);

            // Raw bytes of the current root directory (used portion only).
            var oldLen = (int)tree.Root.DataLength;
            var oldBuf = new byte[oldLen];
            iso.Seek((long)tree.Root.ExtentLba * SectorSize, SeekOrigin.Begin);
            iso.ReadExactly(oldBuf, 0, oldLen);

            var newDir = BuildRelocatedRootDirectory(oldBuf, oldLen, newRecord, fileLba: 0, newDataLength: 0);
            // Placeholder; we fix . / .. after we know the new LBA/length.
            var newDirLength = (uint)newDir.Length;
            var newDirPadded = ((newDir.Length + SectorSize - 1) / SectorSize) * SectorSize;
            var newRootLba = (uint)(iso.Length / SectorSize);

            newDir = BuildRelocatedRootDirectory(oldBuf, oldLen, newRecord, newRootLba, newDirLength);
            if (newDir.Length != newDirLength)
                throw new InvalidOperationException("Root directory size changed after fixing . / .. pointers.");

            iso.Seek(0, SeekOrigin.End);
            await iso.WriteAsync(newDir);
            if (newDirPadded != newDir.Length)
                await iso.WriteAsync(new byte[newDirPadded - newDir.Length]);

            // Volume descriptor's embedded root record (offset 156 in PVD/SVD).
            PatchDirectoryRecord(iso, tree.Root.AbsoluteOffset, newRootLba, newDirLength);

            // Path tables still name the old root extent — keep them in sync.
            PatchPathTableRootExtent(iso, tree.TypeLPathTableLba, tree.PathTableSize, newRootLba, typeM: false);
            PatchPathTableRootExtent(iso, tree.OptionalTypeLPathTableLba, tree.PathTableSize, newRootLba, typeM: false);
            PatchPathTableRootExtent(iso, tree.TypeMPathTableLba, tree.PathTableSize, newRootLba, typeM: true);
            PatchPathTableRootExtent(iso, tree.OptionalTypeMPathTableLba, tree.PathTableSize, newRootLba, typeM: true);

            // First-level subdirectories still point .. at the old root.
            foreach (var child in existing.Where(e => e.IsDirectory && e.Name is not ("." or "..")))
            {
                PatchDotDotInDirectory(iso, child, newRootLba, newDirLength, useJoliet);
            }
        }

        FinalizeIsoGrowth(iso);
        await iso.FlushAsync();
    }

    /// <summary>
    /// Copies <paramref name="oldRootBytes"/> and appends <paramref name="newFileRecord"/>,
    /// rewriting the <c>.</c>/<c>..</c> self-records to <paramref name="fileLba"/> /
    /// <paramref name="newDataLength"/> when those are non-zero.
    /// </summary>
    private static byte[] BuildRelocatedRootDirectory(
        byte[] oldRootBytes, int oldLen, byte[] newFileRecord, uint fileLba, uint newDataLength)
    {
        using var ms = new MemoryStream();
        var pos = 0;
        while (pos < oldLen)
        {
            var lenDr = oldRootBytes[pos];
            if (lenDr == 0)
            {
                pos = ((pos / SectorSize) + 1) * SectorSize;
                continue;
            }

            var record = new byte[lenDr];
            Array.Copy(oldRootBytes, pos, record, 0, lenDr);

            // . and .. are 1-byte identifiers 0x00 / 0x01.
            if (lenDr >= 34 && record[32] == 1 && (record[33] == 0 || record[33] == 1) && fileLba != 0)
            {
                WriteBothEndian(record, 2, fileLba);
                WriteBothEndian(record, 10, newDataLength);
            }

            ms.Write(record);
            pos += lenDr;
        }

        // Sector boundary: a record must not cross. If needed, zero-pad first.
        var need = newFileRecord.Length;
        var offsetInSector = (int)(ms.Length % SectorSize);
        if (offsetInSector + need > SectorSize)
        {
            var pad = SectorSize - offsetInSector;
            ms.Write(new byte[pad]);
        }

        ms.Write(newFileRecord);
        return ms.ToArray();
    }

    private static void PatchDotDotInDirectory(
        Stream iso, DirRecord dir, uint parentLba, uint parentLength, bool isJoliet)
    {
        var entries = ReadDirectory(iso, dir, isJoliet);
        var dotDot = entries.FirstOrDefault(e => e.Name == "..");
        if (dotDot is null) return;
        PatchDirectoryRecord(iso, dotDot.AbsoluteOffset, parentLba, parentLength);
    }

    private static byte[] BuildFileDirectoryRecord(
        uint lba, uint dataLength, string fileName, bool isJoliet, string? rockRidgeName)
    {
        byte[] nameBytes;
        if (isJoliet)
        {
            nameBytes = Encoding.BigEndianUnicode.GetBytes(fileName);
        }
        else
        {
            // ISO9660 file identifiers use uppercase and a version suffix.
            var id = fileName.Contains(';') ? fileName : fileName + ";1";
            nameBytes = Encoding.ASCII.GetBytes(id.ToUpperInvariant());
        }

        var lenFi = nameBytes.Length;
        var namePad = (lenFi % 2 == 0) ? 1 : 0; // len_fi even => one pad byte so SUSP starts even

        byte[] susp = [];
        if (rockRidgeName is not null)
        {
            var nm = Encoding.ASCII.GetBytes(rockRidgeName);
            // NM: 'N''M' len flags name — flags byte = 0
            susp = new byte[5 + nm.Length];
            susp[0] = (byte)'N';
            susp[1] = (byte)'M';
            susp[2] = (byte)susp.Length;
            susp[3] = 1; // version
            susp[4] = 0; // flags
            Array.Copy(nm, 0, susp, 5, nm.Length);
        }

        var lenDr = 33 + lenFi + namePad + susp.Length;
        if (lenDr % 2 != 0) lenDr++; // directory records are even-sized
        var record = new byte[lenDr];
        record[0] = (byte)lenDr;
        record[1] = 0; // EAR length
        WriteBothEndian(record, 2, lba);
        WriteBothEndian(record, 10, dataLength);
        // Recording date/time: seven bytes; leave zeros (acceptable for readers).
        record[25] = 0; // flags: regular file
        record[28] = 1; // volume sequence number LE
        record[29] = 0;
        record[30] = 0;
        record[31] = 1; // BE
        record[32] = (byte)lenFi;
        Array.Copy(nameBytes, 0, record, 33, lenFi);
        if (susp.Length > 0)
            Array.Copy(susp, 0, record, 33 + lenFi + namePad, susp.Length);
        return record;
    }

    private static void PatchDirectoryRecord(Stream dst, long recordOffset, uint lba, uint length)
    {
        var buffer = new byte[16];
        WriteBothEndian(buffer, 0, lba);
        WriteBothEndian(buffer, 8, length);
        dst.Seek(recordOffset + 2, SeekOrigin.Begin); // +2: skip LEN_DR, EAR length bytes
        dst.Write(buffer, 0, buffer.Length);
    }

    private static void WriteBothEndian(byte[] buffer, int offset, uint value)
    {
        var le = BitConverter.GetBytes(value);
        var be = (byte[])le.Clone();
        Array.Reverse(be);
        if (!BitConverter.IsLittleEndian) Array.Reverse(le); // defensive; .NET is always LE in practice
        Array.Copy(le, 0, buffer, offset, 4);
        Array.Copy(be, 0, buffer, offset + 4, 4);
    }

    private sealed record VolumeTree(
        string Name,
        bool IsJoliet,
        DirRecord Root,
        uint PathTableSize,
        uint TypeLPathTableLba,
        uint OptionalTypeLPathTableLba,
        uint TypeMPathTableLba,
        uint OptionalTypeMPathTableLba);

    private static List<VolumeTree> FindVolumeTrees(Stream iso)
    {
        var trees = new List<VolumeTree>();
        var sector = new byte[SectorSize];

        for (var lba = 16; lba < 16 + 32; lba++)
        {
            iso.Seek((long)lba * SectorSize, SeekOrigin.Begin);
            var read = iso.Read(sector, 0, SectorSize);
            if (read < SectorSize) break;

            var type = sector[0];
            if (type == 255) break; // Volume Descriptor Set Terminator
            if (type != 1 && type != 2) continue; // only Primary (1) / Supplementary (2)

            var isJoliet = type == 2 && IsJolietEscape(sector);
            if (type == 2 && !isJoliet) continue; // some other supplementary descriptor we don't understand

            var rootRecordOffset = (long)lba * SectorSize + 156;
            var root = ParseDirRecord(sector, 156, rootRecordOffset, isJoliet);
            if (root is null) continue;

            // ECMA-119 path table fields (LE half first; Type M locations are BE).
            var pathTableSize = ReadBothEndianUInt32(sector, 132);
            var typeL = BitConverter.ToUInt32(sector, 140);
            var optL = BitConverter.ToUInt32(sector, 144);
            var typeM = ReadBigEndianUInt32(sector, 148);
            var optM = ReadBigEndianUInt32(sector, 152);

            trees.Add(new VolumeTree(
                isJoliet ? "Joliet" : "Primary",
                isJoliet,
                root,
                pathTableSize,
                typeL,
                optL,
                typeM,
                optM));
        }

        return trees;
    }

    /// <summary>
    /// After appending extents past the original end: move the hybrid GPT
    /// backup into freshly appended space (never overlapping ISO payloads),
    /// then publish the final size to every ISO9660 volume descriptor.
    /// </summary>
    private static void FinalizeIsoGrowth(Stream iso)
    {
        if (iso.Length % SectorSize != 0)
            throw new InvalidDataException("ISO size is not a whole number of sectors after growth.");

        // GPT first — it may extend the file; volume size must reflect that.
        SyncHybridGptAfterGrow(iso);
        UpdateVolumeSpaceSize(iso);
    }

    private static void UpdateVolumeSpaceSize(Stream iso)
    {
        var sectorCount = (uint)(iso.Length / SectorSize);
        var sector = new byte[SectorSize];

        for (var lba = 16; lba < 16 + 32; lba++)
        {
            iso.Seek((long)lba * SectorSize, SeekOrigin.Begin);
            if (iso.Read(sector, 0, SectorSize) < SectorSize) break;

            var type = sector[0];
            if (type == 255) break;
            // Primary (1), Supplementary/Joliet (2), and Enhanced (2/3) all carry Volume Space Size at 80.
            if (type is not (1 or 2 or 3)) continue;

            WriteBothEndian(sector, 80, sectorCount);
            iso.Seek((long)lba * SectorSize, SeekOrigin.Begin);
            iso.Write(sector, 0, SectorSize);
        }
    }

    private static void PatchPathTableRootExtent(
        Stream iso, uint pathTableLba, uint pathTableSize, uint newRootLba, bool typeM)
    {
        if (pathTableLba == 0 || pathTableSize < 8) return;

        var buf = new byte[8];
        iso.Seek((long)pathTableLba * SectorSize, SeekOrigin.Begin);
        if (iso.Read(buf, 0, 8) < 8) return;

        // Root path-table record: LEN_DI=1, Extent at offset 2, name 0x00.
        if (buf[0] != 1) return;

        if (typeM)
        {
            var be = BitConverter.GetBytes(newRootLba);
            Array.Reverse(be);
            Array.Copy(be, 0, buf, 2, 4);
        }
        else
        {
            Array.Copy(BitConverter.GetBytes(newRootLba), 0, buf, 2, 4);
        }

        iso.Seek((long)pathTableLba * SectorSize, SeekOrigin.Begin);
        iso.Write(buf, 0, 8);
    }

    /// <summary>
    /// Hybrid isohybrid images put a GPT on the ISO. After we append bytes the
    /// backup header is no longer at the end of the disk; firmware then
    /// treats the GPT as invalid and drops the EFI boot entry.
    /// <para>
    /// Important: the backup must be written into <em>newly appended</em> space.
    /// Writing it over the current end of the file destroys the just-added
    /// initrd / relocated root directory — GRUB then reports
    /// <c>/boot not found</c> and the live initrd crashes.
    /// </para>
    /// </summary>
    private static void SyncHybridGptAfterGrow(Stream iso)
    {
        if (iso.Length % GptSectorSize != 0) return;

        var header = new byte[GptSectorSize];
        iso.Seek(GptSectorSize, SeekOrigin.Begin); // LBA 1
        if (iso.Read(header, 0, GptSectorSize) < GptSectorSize) return;
        if (Encoding.ASCII.GetString(header, 0, 8) != "EFI PART") return;

        var headerSize = BitConverter.ToUInt32(header, 12);
        if (headerSize < 92 || headerSize > GptSectorSize) return;

        var partEntryLba = BitConverter.ToUInt64(header, 72);
        var numEntries = BitConverter.ToUInt32(header, 80);
        var entrySize = BitConverter.ToUInt32(header, 84);
        if (numEntries == 0 || entrySize < 128 || partEntryLba == 0) return;

        var partArrayBytes = checked((int)(numEntries * entrySize));
        var partArrayLbas = (partArrayBytes + GptSectorSize - 1) / GptSectorSize;
        var gptTailSectors512 = partArrayLbas + 1; // partition array + backup header

        // Round the GPT tail up so the whole image stays 2048-byte aligned.
        var gptTailSectorsAligned = ((gptTailSectors512 + 3) / 4) * 4;
        var gptTailBytes = gptTailSectorsAligned * GptSectorSize;

        var alternateLba = BitConverter.ToUInt64(header, 32);
        var payloadEnd512 = iso.Length / GptSectorSize;
        var newAlt = (ulong)payloadEnd512 + (ulong)gptTailSectorsAligned - 1;
        if (alternateLba == newAlt && iso.Length >= (long)(newAlt + 1) * GptSectorSize)
            return; // already has a trailing GPT backup past payloads

        var partArray = new byte[partArrayLbas * GptSectorSize];
        iso.Seek((long)partEntryLba * GptSectorSize, SeekOrigin.Begin);
        iso.ReadExactly(partArray, 0, partArrayBytes);

        // Extend past ISO payloads, then place backup GPT in the extension only.
        iso.Seek(0, SeekOrigin.End);
        iso.Write(new byte[gptTailBytes]);

        var newBackupPartLba = newAlt - (ulong)partArrayLbas;

        // Primary header: Alternate LBA → new end. Partition entry LBA unchanged.
        WriteUInt64LE(header, 32, newAlt);
        WriteUInt32LE(header, 16, 0); // CRC must be zero while computing
        WriteUInt32LE(header, 16, Crc32Ieee(header.AsSpan(0, (int)headerSize)));

        iso.Seek(GptSectorSize, SeekOrigin.Begin);
        iso.Write(header, 0, GptSectorSize);

        iso.Seek((long)newBackupPartLba * GptSectorSize, SeekOrigin.Begin);
        iso.Write(partArray, 0, partArray.Length);

        var backup = (byte[])header.Clone();
        WriteUInt64LE(backup, 24, newAlt);       // Current LBA
        WriteUInt64LE(backup, 32, 1);            // Alternate = primary
        WriteUInt64LE(backup, 72, newBackupPartLba);
        WriteUInt32LE(backup, 16, 0);
        WriteUInt32LE(backup, 16, Crc32Ieee(backup.AsSpan(0, (int)headerSize)));

        iso.Seek((long)newAlt * GptSectorSize, SeekOrigin.Begin);
        iso.Write(backup, 0, GptSectorSize);
    }

    private static void WriteUInt32LE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteUInt64LE(byte[] buffer, int offset, ulong value)
    {
        for (var i = 0; i < 8; i++)
            buffer[offset + i] = (byte)(value >> (8 * i));
    }

    private static uint ReadBigEndianUInt32(byte[] buf, int offset) =>
        (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3]);

    /// <summary>ISO 3309 / GPT CRC-32 (reflected poly 0xEDB88320).</summary>
    private static uint Crc32Ieee(ReadOnlySpan<byte> data)
    {
        var table = Crc32Table;
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] Crc32Table = CreateCrc32Table();

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    private static bool IsJolietEscape(byte[] pvdSector)
    {
        // Escape sequence at offset 88, 3 bytes. UCS-2 level 1/2/3.
        var e0 = pvdSector[88];
        var e1 = pvdSector[89];
        var e2 = pvdSector[90];
        return e0 == 0x25 && e1 == 0x2F && (e2 == 0x40 || e2 == 0x43 || e2 == 0x45);
    }

    private static List<DirRecord> ReadDirectory(Stream iso, DirRecord dir, bool isJoliet)
    {
        var entries = new List<DirRecord>();
        var totalBytes = ((int)dir.DataLength + SectorSize - 1) / SectorSize * SectorSize;
        var buffer = new byte[totalBytes];
        iso.Seek((long)dir.ExtentLba * SectorSize, SeekOrigin.Begin);
        iso.ReadExactly(buffer, 0, totalBytes);

        var baseOffset = (long)dir.ExtentLba * SectorSize;
        var pos = 0;
        while (pos < dir.DataLength)
        {
            var lenDr = buffer[pos];
            if (lenDr == 0)
            {
                // Zero-padding to the next sector boundary.
                pos = ((pos / SectorSize) + 1) * SectorSize;
                continue;
            }

            var record = ParseDirRecord(buffer, pos, baseOffset + pos, isJoliet);
            if (record is not null) entries.Add(record);
            pos += lenDr;
        }

        return entries;
    }

    private static DirRecord? ParseDirRecord(byte[] buf, int offset, long absoluteOffset, bool isJoliet)
    {
        if (offset + 34 > buf.Length) return null;
        var lenDr = buf[offset];
        if (lenDr == 0) return null;

        var extentLba = ReadBothEndianUInt32(buf, offset + 2);
        var dataLength = ReadBothEndianUInt32(buf, offset + 10);
        var flags = buf[offset + 25];
        var isDirectory = (flags & 0x02) != 0;
        var lenFi = buf[offset + 32];
        var fiOffset = offset + 33;

        string name;
        if (lenFi == 1 && (buf[fiOffset] == 0 || buf[fiOffset] == 1))
        {
            name = buf[fiOffset] == 0 ? "." : "..";
        }
        else if (isJoliet)
        {
            name = Encoding.BigEndianUnicode.GetString(buf, fiOffset, lenFi);
        }
        else
        {
            name = Encoding.ASCII.GetString(buf, fiOffset, lenFi);
            var susOffset = fiOffset + lenFi + (lenFi % 2 == 0 ? 1 : 0);
            var rockRidgeName = TryReadRockRidgeName(buf, susOffset, offset + lenDr);
            if (rockRidgeName is not null) name = rockRidgeName;
        }

        return new DirRecord(absoluteOffset, lenDr, extentLba, dataLength, isDirectory, name);
    }

    /// <summary>Scans the SUSP system-use area of a directory record for a Rock Ridge "NM" (alternate name) entry.</summary>
    private static string? TryReadRockRidgeName(byte[] buf, int start, int end)
    {
        var sb = new StringBuilder();
        var found = false;
        var pos = start;
        while (pos + 4 <= end)
        {
            var sig0 = buf[pos];
            var sig1 = buf[pos + 1];
            var len = buf[pos + 2];
            if (len < 4 || pos + len > end) break; // not a well-formed SUSP entry; stop scanning

            if (sig0 == 'N' && sig1 == 'M' && len >= 5)
            {
                var nameLen = len - 5;
                sb.Append(Encoding.ASCII.GetString(buf, pos + 5, nameLen));
                found = true;
            }

            pos += len;
        }
        return found ? sb.ToString() : null;
    }

    private static uint ReadBothEndianUInt32(byte[] buf, int offset) =>
        BitConverter.ToUInt32(buf, offset); // little-endian half is first; .NET reads LE natively

    private static string StripVersion(string name)
    {
        var semi = name.IndexOf(';');
        var stripped = semi >= 0 ? name[..semi] : name;
        // Plain (non-Rock-Ridge) ISO9660 names get a trailing "." when there's no extension.
        return stripped.EndsWith('.') ? stripped[..^1] : stripped;
    }
}
