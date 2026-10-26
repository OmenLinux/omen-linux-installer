using OmenPrep.Services;

namespace OmenPrep.Tests;

public class CpioReaderTests
{
    [Fact]
    public void FindEntries_ScansPastLeadingJunk_AndReturnsAcpiOverrideFiles()
    {
        var dsdt = new byte[] { 0x44, 0x53, 0x44, 0x54, 9, 9, 9 };
        var archive = CpioBuilder.BuildAcpiOverrideArchive(new Dictionary<string, byte[]>
        {
            ["dsdt.aml"] = dsdt,
        });

        // Simulate a concatenated initramfs: compressed/original bytes, then our cpio.
        var leadingJunk = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0xAA, 0xBB, 0xCC };
        var combined = new byte[leadingJunk.Length + archive.Length];
        Array.Copy(leadingJunk, combined, leadingJunk.Length);
        Array.Copy(archive, 0, combined, leadingJunk.Length, archive.Length);

        var entries = CpioReader.FindEntries(combined, "kernel/firmware/acpi");

        Assert.Contains(entries, e => e.Name == "kernel/firmware/acpi");
        var file = Assert.Single(entries, e => e.Name == "kernel/firmware/acpi/dsdt.aml");
        Assert.Equal(dsdt, file.Data);
        Assert.True(file.Offset >= leadingJunk.Length);
    }

    [Fact]
    public void FindEntries_WithoutFilter_IncludesDirectoriesAndTrailerIsSkippedByNameFilter()
    {
        var archive = CpioBuilder.BuildAcpiOverrideArchive(new Dictionary<string, byte[]>
        {
            ["dsdt.aml"] = [1, 2, 3],
        });

        var all = CpioReader.FindEntries(archive);
        Assert.Contains(all, e => e.Name == "kernel");
        Assert.Contains(all, e => e.Name == "kernel/firmware/acpi/dsdt.aml");
        // TRAILER!!! is a valid entry name; reader returns it when unfiltered.
        Assert.Contains(all, e => e.Name == "TRAILER!!!");

        var filtered = CpioReader.FindEntries(archive, "kernel/firmware/acpi/");
        Assert.DoesNotContain(filtered, e => e.Name == "TRAILER!!!");
        Assert.DoesNotContain(filtered, e => e.Name == "kernel");
    }
}
