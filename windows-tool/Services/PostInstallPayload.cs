using System.IO;
using System.Text;

namespace OmenPrep.Services;

/// <summary>
/// ISO-root <c>post-install-omen.sh</c>: after first boot into the installed
/// system, permanently hooks the DSDT override for kernel upgrades and
/// installs the RGB keyboard DKMS driver. Separate from
/// <see cref="AcpiPersistPayload"/> (live-session apply → /mnt).
/// </summary>
public static class PostInstallPayload
{
    public const string Iso9660FileName = "POSTINST.SH;1";
    public const string JolietFileName = "post-install-omen.sh";
    public const string RockRidgeFileName = "post-install-omen.sh";
    public const string IsoPathHint = "/post-install-omen.sh";

    private const string EmbeddedName = "OmenPrep.post-install-omen.sh";

    public static byte[] ReadScriptBytes()
    {
        var besideExe = Path.Combine(AppContext.BaseDirectory, "Resources", "post-install-omen.sh");
        if (File.Exists(besideExe))
            return NormalizeUnix(File.ReadAllBytes(besideExe));

        var asm = typeof(PostInstallPayload).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedName)
            ?? throw new FileNotFoundException(
                $"Missing embedded resource '{EmbeddedName}' and no file at '{besideExe}'.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return NormalizeUnix(ms.ToArray());
    }

    public static string ReadScriptText() => Encoding.UTF8.GetString(ReadScriptBytes());

    private static byte[] NormalizeUnix(byte[] raw)
    {
        var text = Encoding.UTF8.GetString(raw).Replace("\r\n", "\n").Replace("\r", "\n");
        return Encoding.UTF8.GetBytes(text);
    }
}
