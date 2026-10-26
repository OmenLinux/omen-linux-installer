using System.Text;
using OmenPrep.Services;

namespace OmenPrep.Tests;

public class CpioBuilderTests
{
    /// <summary>Minimal newc-format cpio reader, test-only, to verify CpioBuilder's output independently of the writer.</summary>
    private sealed record CpioEntry(string Name, int Mode, byte[] Data);

    private static List<CpioEntry> ParseNewc(byte[] bytes)
    {
        var entries = new List<CpioEntry>();
        var pos = 0;
        while (pos < bytes.Length)
        {
            var header = Encoding.ASCII.GetString(bytes, pos, 110);
            if (header[..6] != "070701")
                throw new InvalidDataException($"Bad magic at {pos}: {header[..6]}");

            int Field(int offset) => Convert.ToInt32(header.Substring(offset, 8), 16);

            var mode = Field(14);
            var filesize = Field(54);
            var namesize = Field(94);

            pos += 110;
            var name = Encoding.ASCII.GetString(bytes, pos, namesize - 1); // drop trailing NUL
            pos += namesize;
            pos = Align4(pos);

            if (name == "TRAILER!!!") break;

            var data = new byte[filesize];
            Array.Copy(bytes, pos, data, 0, filesize);
            pos += filesize;
            pos = Align4(pos);

            entries.Add(new CpioEntry(name, mode, data));
        }
        return entries;
    }

    private static int Align4(int pos) => (pos % 4 == 0) ? pos : pos + (4 - pos % 4);

    [Fact]
    public void BuildsDirectoriesThenFilesThenTrailer()
    {
        var dsdt = new byte[] { 0x44, 0x53, 0x44, 0x54, 1, 2, 3 }; // "DSDT" + junk
        var archive = CpioBuilder.BuildAcpiOverrideArchive(new Dictionary<string, byte[]>
        {
            ["dsdt.aml"] = dsdt,
        });

        var entries = ParseNewc(archive);

        Assert.Equal(["kernel", "kernel/firmware", "kernel/firmware/acpi", "kernel/firmware/acpi/dsdt.aml"],
            entries.Select(e => e.Name).ToArray());

        var dsdtEntry = entries.Single(e => e.Name == "kernel/firmware/acpi/dsdt.aml");
        Assert.Equal(dsdt, dsdtEntry.Data);
        Assert.Equal(Convert.ToInt32("100644", 8), dsdtEntry.Mode);

        var dirEntry = entries.Single(e => e.Name == "kernel");
        Assert.Equal(Convert.ToInt32("40755", 8), dirEntry.Mode);
        Assert.Empty(dirEntry.Data);
    }

    [Fact]
    public void SupportsMultipleTables()
    {
        var archive = CpioBuilder.BuildAcpiOverrideArchive(new Dictionary<string, byte[]>
        {
            ["dsdt.aml"] = [1, 2, 3],
            ["ssdt1.aml"] = [4, 5],
        });

        var entries = ParseNewc(archive);
        var names = entries.Select(e => e.Name).ToArray();
        Assert.Contains("kernel/firmware/acpi/dsdt.aml", names);
        Assert.Contains("kernel/firmware/acpi/ssdt1.aml", names);
    }

    [Fact]
    public void OutputIsPaddedTo512ByteBoundary()
    {
        var archive = CpioBuilder.BuildAcpiOverrideArchive(new Dictionary<string, byte[]>
        {
            ["dsdt.aml"] = [1, 2, 3, 4, 5],
        });

        Assert.Equal(0, archive.Length % 512);
        // Parsing must still succeed despite the trailing zero padding after TRAILER!!!.
        var entries = ParseNewc(archive);
        Assert.NotEmpty(entries);
    }

    [Fact]
    public void AcpiOverrideArchive_IsOnlyKernelFirmwareAcpi()
    {
        var tables = new Dictionary<string, byte[]> { ["dsdt.aml"] = [0x44, 0x53, 0x44, 0x54, 1, 2] };
        var acpiOnly = CpioBuilder.BuildAcpiOverrideArchive(tables);
        var names = ParseNewc(acpiOnly).Select(e => e.Name).ToArray();
        Assert.Equal(
            ["kernel", "kernel/firmware", "kernel/firmware/acpi", "kernel/firmware/acpi/dsdt.aml"],
            names);
    }
}
