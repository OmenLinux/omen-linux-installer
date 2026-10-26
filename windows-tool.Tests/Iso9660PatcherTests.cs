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
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
