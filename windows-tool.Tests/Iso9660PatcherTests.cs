using DiscUtils.Iso9660;
using OmenPrep.Services;

namespace OmenPrep.Tests;

public class Iso9660PatcherTests
{
    private static string BuildSyntheticIso(string isoPath, byte[] initrdContent, byte[] otherFileContent)
    {
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "TESTISO" };
        builder.AddDirectory("BOOT");
        builder.AddFile("BOOT\\INITRD.IMG", initrdContent);
        builder.AddFile("README.TXT", otherFileContent);
        builder.Build(isoPath);
        return isoPath;
    }

    [Fact]
    public void LocateFile_FindsFileInBothPrimaryAndJolietTrees()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omen-prep-iso-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var isoPath = BuildSyntheticIso(Path.Combine(dir, "test.iso"), [1, 2, 3, 4, 5], [9, 9]);

            using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read);
            var located = Iso9660Patcher.LocateFile(stream, "/boot/initrd.img");

            Assert.Equal(2, located.Count); // Primary + Joliet
            Assert.Contains(located, l => l.TreeName == "Primary");
            Assert.Contains(located, l => l.TreeName == "Joliet");
            Assert.All(located, l => Assert.Equal(5u, l.DataLength));

            // Cross-check against DiscUtils' own (independent) reader.
            stream.Seek(0, SeekOrigin.Begin);
            using var reader = new CDReader(stream, joliet: false);
            Assert.True(reader.FileExists(@"\BOOT\INITRD.IMG"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddRootFileAsync_AddsReadableFileToPrimaryAndJoliet()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omen-prep-iso-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var isoPath = BuildSyntheticIso(Path.Combine(dir, "test.iso"), [1, 2, 3], [9, 9]);
            var script = "#!/bin/bash\necho omen-keyboard\n"u8.ToArray();

            await Iso9660Patcher.AddRootFileAsync(
                isoPath, "INSTALL.SH;1", "install-omen-keyboard.sh", script, "install-omen-keyboard.sh");

            using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read);
            AssertVolumeSpaceMatchesFileSize(stream);

            using var joliet = new CDReader(stream, joliet: true);
            using var js = joliet.OpenFile(@"\install-omen-keyboard.sh", FileMode.Open);
            using var jms = new MemoryStream();
            js.CopyTo(jms);
            Assert.Equal(script, jms.ToArray());

            stream.Seek(0, SeekOrigin.Begin);
            using var primary = new CDReader(stream, joliet: false);
            // Without Rock Ridge on a DiscUtils-built ISO the Primary name is INSTALL.SH.
            Assert.True(
                primary.FileExists(@"\INSTALL.SH") || primary.FileExists(@"\INSTALL.SH;1"),
                "Primary tree should expose INSTALL.SH");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void AssertVolumeSpaceMatchesFileSize(Stream iso)
    {
        const int sectorSize = 2048;
        Assert.Equal(0, iso.Length % sectorSize);
        var expected = (uint)(iso.Length / sectorSize);
        var sector = new byte[sectorSize];
        iso.Seek(16L * sectorSize, SeekOrigin.Begin);
        iso.ReadExactly(sector);
        var volSpace = BitConverter.ToUInt32(sector, 80);
        Assert.Equal(expected, volSpace);
    }

    [Fact]
    public async Task InjectPrependedFileAsync_GrowsFileAndPreservesEverythingElse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omen-prep-iso-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var initrdContent = new byte[] { 0x10, 0x20, 0x30 };
            var otherContent = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
            var sourceIso = BuildSyntheticIso(Path.Combine(dir, "source.iso"), initrdContent, otherContent);
            var destIso = Path.Combine(dir, "patched.iso");

            var prefix = new byte[] { 0xFE, 0xED, 0xFA, 0xCE };
            await Iso9660Patcher.InjectPrependedFileAsync(sourceIso, destIso, "/boot/initrd.img", prefix);

            using var destStream = new FileStream(destIso, FileMode.Open, FileAccess.Read);
            using var reader = new CDReader(destStream, joliet: false);

            // The patched file now reads as prefix + original bytes.
            using var initrdStream = reader.OpenFile(@"\BOOT\INITRD.IMG", FileMode.Open);
            using var ms = new MemoryStream();
            initrdStream.CopyTo(ms);
            Assert.Equal([..prefix, ..initrdContent], ms.ToArray());

            // Everything else in the image is untouched.
            using var otherStream = reader.OpenFile(@"\README.TXT", FileMode.Open);
            using var ms2 = new MemoryStream();
            otherStream.CopyTo(ms2);
            Assert.Equal(otherContent, ms2.ToArray());

            // The Joliet tree agrees too (both records were patched).
            destStream.Seek(0, SeekOrigin.Begin);
            using var jolietReader = new CDReader(destStream, joliet: true);
            using var jolietInitrd = jolietReader.OpenFile(@"\boot\initrd.img", FileMode.Open);
            using var ms3 = new MemoryStream();
            jolietInitrd.CopyTo(ms3);
            Assert.Equal([..prefix, ..initrdContent], ms3.ToArray());

            destStream.Seek(0, SeekOrigin.Begin);
            AssertVolumeSpaceMatchesFileSize(destStream);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task InjectPrependedFileAsync_UpdatesGptBackupToNewEndOfImage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omen-prep-iso-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var initrdContent = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var sourceIso = BuildSyntheticIso(Path.Combine(dir, "source.iso"), initrdContent, [9]);
            // Graft a minimal protective GPT so SyncHybridGptAfterGrow has work to do.
            GraftMinimalGpt(sourceIso);

            var destIso = Path.Combine(dir, "patched.iso");
            var prefix = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
            await Iso9660Patcher.InjectPrependedFileAsync(sourceIso, destIso, "/boot/initrd.img", prefix);

            using var fs = new FileStream(destIso, FileMode.Open, FileAccess.Read);
            AssertVolumeSpaceMatchesFileSize(fs);

            var header = new byte[512];
            fs.Seek(512, SeekOrigin.Begin);
            fs.ReadExactly(header);
            Assert.Equal("EFI PART", System.Text.Encoding.ASCII.GetString(header, 0, 8));
            var altLba = BitConverter.ToUInt64(header, 32);
            var expectedAlt = (ulong)(fs.Length / 512) - 1;
            Assert.Equal(expectedAlt, altLba);

            // Backup header must live at Alternate LBA with a matching signature.
            var backup = new byte[512];
            fs.Seek((long)altLba * 512, SeekOrigin.Begin);
            fs.ReadExactly(backup);
            Assert.Equal("EFI PART", System.Text.Encoding.ASCII.GetString(backup, 0, 8));
            Assert.Equal(1ul, BitConverter.ToUInt64(backup, 32)); // alternate → primary

            // GPT must not have clobbered the relocated initrd (the /boot-not-found bug).
            fs.Seek(0, SeekOrigin.Begin);
            using var reader = new CDReader(fs, joliet: false);
            using var initrd = reader.OpenFile(@"\BOOT\INITRD.IMG", FileMode.Open);
            using var ms = new MemoryStream();
            initrd.CopyTo(ms);
            Assert.Equal([..prefix, ..initrdContent], ms.ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Writes a tiny valid-enough GPT (header + 128 empty entries) for grow tests.</summary>
    private static void GraftMinimalGpt(string isoPath)
    {
        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite);
        const uint numEntries = 128;
        const uint entrySize = 128;
        var partBytes = (int)(numEntries * entrySize); // 16384
        var partLbas = (partBytes + 511) / 512;         // 32
        // Append tail space so the backup GPT does not clobber ISO9660 payloads.
        // Keep the image 2048-byte aligned (ISO sector size); 36×512 = 9×2048.
        var tailSectors512 = ((partLbas + 1 + 3) / 4) * 4; // round up to multiple of 4
        fs.SetLength(fs.Length + tailSectors512 * 512);

        var sectors = fs.Length / 512;
        var alt = (ulong)sectors - 1;
        var backupPartLba = alt - (ulong)partLbas;

        var partArray = new byte[partLbas * 512];
        // Primary partition array at LBA 2 (ISO system area; DiscUtils leaves it empty).
        fs.Seek(2 * 512, SeekOrigin.Begin);
        fs.Write(partArray);

        var header = new byte[512];
        System.Text.Encoding.ASCII.GetBytes("EFI PART").CopyTo(header, 0);
        header[8] = 0x00; header[9] = 0x00; header[10] = 0x01; header[11] = 0x00; // revision 1.0
        BitConverter.GetBytes(92u).CopyTo(header, 12); // header size
        // CRC at 16 filled below
        BitConverter.GetBytes(1ul).CopyTo(header, 24);           // current LBA
        BitConverter.GetBytes(alt).CopyTo(header, 32);           // alternate
        BitConverter.GetBytes(34ul).CopyTo(header, 40);          // first usable
        BitConverter.GetBytes(backupPartLba - 1).CopyTo(header, 48); // last usable
        // disk GUID left zero
        BitConverter.GetBytes(2ul).CopyTo(header, 72);           // part entries LBA
        BitConverter.GetBytes(numEntries).CopyTo(header, 80);
        BitConverter.GetBytes(entrySize).CopyTo(header, 84);
        // Partition array CRC left 0 (empty entries → CRC 0 for all-zero is fine enough for our relocator)

        // Compute header CRC with field zeroed.
        for (var i = 16; i < 20; i++) header[i] = 0;
        var crc = Crc32IeeeForTest(header.AsSpan(0, 92));
        BitConverter.GetBytes(crc).CopyTo(header, 16);

        fs.Seek(512, SeekOrigin.Begin);
        fs.Write(header);

        // Backup partition array + header at end.
        fs.Seek((long)backupPartLba * 512, SeekOrigin.Begin);
        fs.Write(partArray);

        var backup = (byte[])header.Clone();
        BitConverter.GetBytes(alt).CopyTo(backup, 24);
        BitConverter.GetBytes(1ul).CopyTo(backup, 32);
        BitConverter.GetBytes(backupPartLba).CopyTo(backup, 72);
        for (var i = 16; i < 20; i++) backup[i] = 0;
        BitConverter.GetBytes(Crc32IeeeForTest(backup.AsSpan(0, 92))).CopyTo(backup, 16);
        fs.Seek((long)alt * 512, SeekOrigin.Begin);
        fs.Write(backup);
    }

    private static uint Crc32IeeeForTest(ReadOnlySpan<byte> data)
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var j = 0; j < 8; j++)
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            table[i] = c;
        }
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
