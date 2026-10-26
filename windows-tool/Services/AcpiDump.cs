using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace OmenPrep.Services;

public sealed record DumpedTable(
    string TableType,
    string RegistryPath,
    string ValueName,
    string Signature,
    int Length,
    string Sha256,
    string FileName);

/// <summary>
/// Dumps raw ACPI tables (DSDT/SSDT) straight from the registry.
///
/// Windows caches the firmware's ACPI tables under
/// HKLM\HARDWARE\ACPI\&lt;DSDT|SSDT&gt;\&lt;OEM ID&gt;\&lt;OEM Table ID&gt;\&lt;Revision&gt;,
/// one REG_BINARY value per table instance. Reading them needs no kernel
/// driver and no admin rights (verified against a real Omen 16-u0xxx,
/// BIOS F.39: the leaf value's first four bytes are the ACPI table
/// signature, e.g. "DSDT"). This is what RwEverything/SSDTTime do under
/// the hood on Windows; we skip the driver entirely and just read the
/// registry.
/// </summary>
public static class AcpiDumper
{
    private const string AcpiRoot = @"HARDWARE\ACPI";
    private static readonly string[] TableTypes = ["DSDT", "SSDT"];

    public static List<DumpedTable> Dump(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var results = new List<DumpedTable>();

        foreach (var tableType in TableTypes)
        {
            var root = $@"{AcpiRoot}\{tableType}";
            foreach (var leafPath in WalkLeafKeys(root))
            {
                using var key = Registry.LocalMachine.OpenSubKey(leafPath);
                if (key is null) continue;

                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValueKind(valueName) != RegistryValueKind.Binary) continue;
                    if (key.GetValue(valueName) is not byte[] data || data.Length == 0) continue;

                    var signature = Encoding.ASCII.GetString(data, 0, Math.Min(4, data.Length));
                    var sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
                    var safeSuffix = leafPath[(AcpiRoot.Length + 1)..].Replace('\\', '_');
                    var valueLabel = string.IsNullOrEmpty(valueName) ? "default" : valueName;
                    var fileName = $"{tableType}_{safeSuffix}_{valueLabel}.aml";

                    File.WriteAllBytes(Path.Combine(outputDir, fileName), data);

                    results.Add(new DumpedTable(
                        TableType: tableType,
                        RegistryPath: leafPath,
                        ValueName: valueLabel,
                        Signature: signature,
                        Length: data.Length,
                        Sha256: sha256,
                        FileName: fileName));
                }
            }
        }

        return results;
    }

    /// <summary>Yields every registry path under <paramref name="path"/> that has at least one value.</summary>
    private static IEnumerable<string> WalkLeafKeys(string path)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        if (key is null) yield break;

        if (key.ValueCount > 0)
        {
            yield return path;
        }

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            foreach (var leaf in WalkLeafKeys($@"{path}\{subKeyName}"))
            {
                yield return leaf;
            }
        }
    }
}
