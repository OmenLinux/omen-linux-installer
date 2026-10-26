using System.IO;
using System.Reflection;
using System.Text;

namespace OmenPrep.Services;

/// <summary>
/// ISO-root helpers: <c>apply-omen-acpi.sh</c> installs ACPI into the
/// target (Fedora: dracut acpi_override; others: early initrd / prepend).
/// </summary>
public static class AcpiPersistPayload
{
    public const string Iso9660FileName = "APPLY.SH;1";
    public const string JolietFileName = "apply-omen-acpi.sh";
    public const string RockRidgeFileName = "apply-omen-acpi.sh";
    public const string IsoPathHint = "/apply-omen-acpi.sh";

    public const string CpioIso9660FileName = "OMENACPI.CPI;1";
    public const string CpioJolietFileName = "omen-acpi.cpio";
    public const string CpioRockRidgeFileName = "omen-acpi.cpio";

    private static byte[] ReadResource(string fileName, string embeddedLogicalName)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Resources", fileName);
        if (File.Exists(beside))
            return NormalizeUnix(File.ReadAllBytes(beside));

        var asm = typeof(AcpiPersistPayload).Assembly;
        using var stream = asm.GetManifestResourceStream(embeddedLogicalName)
            ?? throw new FileNotFoundException($"Missing embedded resource '{embeddedLogicalName}'.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return NormalizeUnix(ms.ToArray());
    }

    private static byte[] NormalizeUnix(byte[] raw)
    {
        var text = Encoding.UTF8.GetString(raw).Replace("\r\n", "\n").Replace("\r", "\n");
        return Encoding.UTF8.GetBytes(text);
    }

    public static byte[] ReadApplyScript() =>
        ReadResource("apply-omen-acpi.sh", "OmenPrep.apply-omen-acpi.sh");
}
