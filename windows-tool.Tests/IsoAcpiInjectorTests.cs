using System.Security.Cryptography;
using DiscUtils.Iso9660;
using OmenPrep.Services;

namespace OmenPrep.Tests;

public class IsoAcpiInjectorTests
{
    [Fact]
    public async Task InjectAsync_FindsInitramfsByFallbackScan_AndAppendsAcpiTable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omen-prep-injector-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            // A layout not in the known candidate list, to exercise the filename-scan fallback.
            var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "TESTISO" };
            builder.AddDirectory("WEIRD");
            builder.AddDirectory("WEIRD\\PATH");
            var originalInitrd = new byte[] { 1, 2, 3, 4 };
            builder.AddFile("WEIRD\\PATH\\MYINITRAMFS.IMG", originalInitrd);
            var sourceIso = Path.Combine(dir, "source.iso");
            builder.Build(sourceIso);

            var found = IsoAcpiInjector.FindInitramfs(sourceIso);
            Assert.NotNull(found);
            Assert.Contains("MYINITRAMFS", found, StringComparison.OrdinalIgnoreCase);

            var destIso = Path.Combine(dir, "patched.iso");
            var dsdt = new byte[] { 0x44, 0x53, 0x44, 0x54, 9, 9, 9 };
            var result = await IsoAcpiInjector.InjectAsync(sourceIso, destIso,
                new Dictionary<string, byte[]> { ["dsdt.aml"] = dsdt });

            Assert.Equal(originalInitrd.Length, result.OriginalInitramfsLength);
            Assert.True(result.InjectedBytes > 0);
            Assert.True(result.ApplyScriptAdded);

            using var destStream = new FileStream(destIso, FileMode.Open, FileAccess.Read);
            using var reader = new CDReader(destStream, joliet: false);
            using var stream = reader.OpenFile(found!.Replace('/', '\\'), FileMode.Open);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);

            var resultBytes = ms.ToArray();
            Assert.True(resultBytes.Length > originalInitrd.Length);
            // ACPI override cpio is prepended; original initramfs bytes follow it.
            Assert.Equal(originalInitrd, resultBytes[^originalInitrd.Length..]);
            var prependedRegion = resultBytes[..^originalInitrd.Length];
            var containsDsdt = Enumerable.Range(0, prependedRegion.Length - dsdt.Length + 1)
                .Any(i => prependedRegion.Skip(i).Take(dsdt.Length).SequenceEqual(dsdt));
            Assert.True(containsDsdt, "Prepended region should contain the DSDT bytes.");
            Assert.Equal(result.InjectedBytes, prependedRegion.Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyAsync_FindsInjectedAcpiTableViaCpioReader()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omen-prep-verify-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "TESTISO" };
            builder.AddDirectory("WEIRD");
            builder.AddDirectory("WEIRD\\PATH");
            // Non-cpio junk first so CpioReader must scan past the original initramfs bytes.
            var originalInitrd = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 1, 2, 3, 4, 5 };
            builder.AddFile("WEIRD\\PATH\\MYINITRAMFS.IMG", originalInitrd);
            var sourceIso = Path.Combine(dir, "source.iso");
            builder.Build(sourceIso);

            var destIso = Path.Combine(dir, "patched.iso");
            var dsdt = new byte[] { 0x44, 0x53, 0x44, 0x54, 9, 9, 9 };
            var ssdt = new byte[] { 0x53, 0x53, 0x44, 0x54, 1, 2 };
            var injected = await IsoAcpiInjector.InjectAsync(sourceIso, destIso,
                new Dictionary<string, byte[]>
                {
                    ["dsdt.aml"] = dsdt,
                    ["ssdt1.aml"] = ssdt,
                });
            Assert.True(injected.ApplyScriptAdded);

            var verified = await IsoAcpiInjector.VerifyAsync(destIso);

            Assert.Contains("MYINITRAMFS", verified.InitramfsPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(verified.InitramfsLength > originalInitrd.Length);

            var dsdtTable = Assert.Single(verified.Tables, t =>
                t.Name.Equals("kernel/firmware/acpi/dsdt.aml", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(dsdt.Length, dsdtTable.Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(dsdt)).ToLowerInvariant(), dsdtTable.Sha256);

            var ssdtTable = Assert.Single(verified.Tables, t =>
                t.Name.Equals("kernel/firmware/acpi/ssdt1.aml", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(ssdt.Length, ssdtTable.Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(ssdt)).ToLowerInvariant(), ssdtTable.Sha256);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
