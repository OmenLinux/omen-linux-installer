using System.IO;
using System.Security.Cryptography;
using DiscUtils.Iso9660;

namespace OmenPrep.Services;

public sealed record IsoInjectionResult(
    string FoundPath,
    long OriginalInitramfsLength,
    long InjectedBytes,
    string OutputIsoPath,
    bool PostInstallScriptAdded,
    bool ApplyScriptAdded);

public sealed record VerifiedAcpiTable(string Name, int Length, string Sha256);

public sealed record IsoVerifyResult(string InitramfsPath, long InitramfsLength, List<VerifiedAcpiTable> Tables);

/// <summary>
/// Prepends an ACPI-only uncompressed cpio to the ISO initramfs (live boot),
/// and ships <c>omen-acpi.cpio</c>, live-session <c>apply-omen-acpi.sh</c>, and
/// booted-OS <c>post-install-omen.sh</c> on the ISO root.
/// </summary>
public static class IsoAcpiInjector
{
    private static readonly string[] CandidatePaths =
    [
        "/arch/boot/x86_64/initramfs-linux.img",
        "/arch/boot/x86_64/initramfs-linux-zen.img",
        "/arch/boot/x86_64/initramfs-linux-hardened.img",
        "/casper/initrd",
        "/casper/initrd.gz",
        "/casper/initrd.lz",
        "/install/initrd.gz",
        "/install.amd/initrd.gz",
        "/images/pxeboot/initrd.img",
        "/isolinux/initrd.img",
        "/LiveOS/initrd.img",
    ];

    private static string ToDiscUtilsPath(string p) => p.Replace('/', '\\');

    public static string? FindInitramfs(string isoPath)
    {
        using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new CDReader(stream, joliet: false);

        foreach (var candidate in CandidatePaths)
        {
            if (reader.FileExists(ToDiscUtilsPath(candidate))) return candidate;
        }

        return FindByNameScan(reader, "")?.Replace('\\', '/');
    }

    private static string? FindByNameScan(CDReader reader, string dir)
    {
        foreach (var file in reader.GetFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (name.Contains("initrd", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("initramfs", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        foreach (var subDir in reader.GetDirectories(dir))
        {
            var found = FindByNameScan(reader, subDir);
            if (found is not null) return found;
        }

        return null;
    }

    public static async Task<IsoInjectionResult> InjectAsync(
        string sourceIsoPath, string destIsoPath, IReadOnlyDictionary<string, byte[]> acpiTables)
    {
        var initramfsPath = FindInitramfs(sourceIsoPath)
            ?? throw new InvalidOperationException(
                "Couldn't find an initramfs/initrd file on this ISO. " +
                "This distro's layout isn't one omen-prep recognizes yet.");

        long originalLength;
        using (var stream = new FileStream(sourceIsoPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new CDReader(stream, joliet: false))
        {
            originalLength = reader.GetFileLength(ToDiscUtilsPath(initramfsPath));
        }

        var acpiCpio = CpioBuilder.BuildAcpiOverrideArchive(acpiTables);
        await Iso9660Patcher.InjectPrependedFileAsync(sourceIsoPath, destIsoPath, initramfsPath, acpiCpio);

        // Root helpers are best-effort for live/post-install convenience; the
        // initramfs prepend above is what makes the live session bootable with ACPI.
        var applyAdded = false;
        try
        {
            await Iso9660Patcher.AddRootFileAsync(
                destIsoPath,
                AcpiPersistPayload.CpioIso9660FileName,
                AcpiPersistPayload.CpioJolietFileName,
                acpiCpio,
                AcpiPersistPayload.CpioRockRidgeFileName);

            if (acpiTables.TryGetValue("dsdt.aml", out var dsdtBytes))
            {
                await Iso9660Patcher.AddRootFileAsync(
                    destIsoPath, "DSDT.AML;1", "dsdt.aml", dsdtBytes, "dsdt.aml");
            }

            await Iso9660Patcher.AddRootFileAsync(
                destIsoPath,
                AcpiPersistPayload.Iso9660FileName,
                AcpiPersistPayload.JolietFileName,
                AcpiPersistPayload.ReadApplyScript(),
                AcpiPersistPayload.RockRidgeFileName);
            applyAdded = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"ISO root helpers not added: {ex.Message}");
        }

        var postInstallAdded = false;
        try
        {
            await Iso9660Patcher.AddRootFileAsync(
                destIsoPath,
                PostInstallPayload.Iso9660FileName,
                PostInstallPayload.JolietFileName,
                PostInstallPayload.ReadScriptBytes(),
                PostInstallPayload.RockRidgeFileName);
            postInstallAdded = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Post-install script not added to ISO: {ex.Message}");
        }

        return new IsoInjectionResult(
            initramfsPath, originalLength, acpiCpio.Length, destIsoPath, postInstallAdded, applyAdded);
    }

    public static async Task<IsoVerifyResult> VerifyAsync(string isoPath)
    {
        var initramfsPath = FindInitramfs(isoPath)
            ?? throw new InvalidOperationException("Couldn't find an initramfs/initrd file on this ISO.");

        byte[] data;
        using (var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new CDReader(stream, joliet: false))
        using (var fileStream = reader.OpenFile(ToDiscUtilsPath(initramfsPath), FileMode.Open))
        using (var ms = new MemoryStream())
        {
            await fileStream.CopyToAsync(ms);
            data = ms.ToArray();
        }

        var entries = CpioReader.FindEntries(data, "kernel/firmware/acpi/")
            .Where(e => e.Data.Length > 0);
        var tables = entries.Select(e => new VerifiedAcpiTable(
            e.Name, e.Data.Length, Convert.ToHexString(SHA256.HashData(e.Data)).ToLowerInvariant())).ToList();

        return new IsoVerifyResult(initramfsPath, data.Length, tables);
    }
}
