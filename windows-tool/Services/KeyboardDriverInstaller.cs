using System.IO;
using System.Reflection;
using System.Text;

namespace OmenPrep.Services;

/// <summary>
/// Locates the post-install omen-rgb-keyboard helper script that gets
/// copied onto the ISO root. After the user installs Linux they run it
/// once (sudo) to clone, DKMS-install, and blacklist hp_wmi.
/// </summary>
public static class KeyboardDriverInstaller
{
    public const string Iso9660FileName = "INSTALL.SH;1";
    public const string JolietFileName = "install-omen-keyboard.sh";
    public const string RockRidgeFileName = "install-omen-keyboard.sh";

    /// <summary>POSIX path users will see on a Joliet/Rock-Ridge mount.</summary>
    public const string IsoPathHint = "/install-omen-keyboard.sh";

    private const string EmbeddedName = "OmenPrep.install-omen-keyboard.sh";

    public static byte[] ReadScriptBytes()
    {
        var besideExe = Path.Combine(AppContext.BaseDirectory, "Resources", "install-omen-keyboard.sh");
        if (File.Exists(besideExe))
            return File.ReadAllBytes(besideExe);

        var asm = typeof(KeyboardDriverInstaller).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedName)
            ?? throw new FileNotFoundException(
                $"Missing embedded resource '{EmbeddedName}' and no file at '{besideExe}'.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static string ReadScriptText() => Encoding.UTF8.GetString(ReadScriptBytes());
}
