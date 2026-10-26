using System.Text.RegularExpressions;

namespace OmenPrep.Services;

/// <summary>
/// Common HP OMEN DSDT patches for Linux. Same pattern across models: bump
/// OEM revision, add _OSI("Linux") mirrors of Windows blocks, remove broken
/// HPIC0004 where present, disable broken PNOT().
///
/// Ported from omen-web's src/lib/acpi/omen-dsdt-patch.ts (the same logic
/// that runs client-side in the browser via iasl-wasm) so this app can run
/// the full disassemble → patch → recompile pipeline itself, instead of
/// sending the user to a browser tab.
/// </summary>
public static class OmenDsdtPatch
{
    public enum PatchStepResult { Applied, Skipped, Warning }

    public sealed record PatchStep(string Id, PatchStepResult Result, string? Detail);

    public sealed record PatchReport(string Dsl, List<PatchStep> Steps, bool LooksLikeHpOmen);

    public static readonly Dictionary<string, (string Title, string Description)> Checklist = new()
    {
        ["revision"] = ("Bump DSDT OEM revision", "Increments the DefinitionBlock revision field by one."),
        ["linux-osys"] = ("Linux OS identity (OSYS)", "Adds _OSI(\"Linux\") with the same OSYS value as the newest Windows block."),
        ["ic04"] = ("Remove HPIC0004 device", "Deletes the broken _SB.PC00.IC04 (HPIC0004) device node."),
        ["pnot"] = ("Disable PNOT() calls", "Comments out PNOT() invocations that break on Linux."),
        ["linux-local1"] = ("Linux EC OS type (Local1)", "Adds _OSI(\"Linux\") with the same Local1 value as the newest Windows block (OSTY)."),
    };

    private static readonly Regex DefinitionBlockDsdt = new(
        @"DefinitionBlock\s*\(\s*""""\s*,\s*""DSDT""\s*,\s*\d+\s*,\s*""[^""]*""\s*,\s*""[^""]*""\s*,\s*(0x[0-9A-Fa-f]+)\s*\)");

    private static readonly Regex WindowsOsys = new(
        @"(\s*)If\s*\(\s*_OSI\s*\(\s*""Windows (\d{4})""\s*\)\s*\)\s*\{[\s\n]*OSYS\s*=\s*(0x[0-9A-Fa-f]+)[\s\n]*\}");

    private static readonly Regex WindowsLocal1 = new(
        @"(\s*)If\s*\(\s*_OSI\s*\(\s*""Windows (\d{4})""\s*\)\s*\)\s*\{[\s\n]*Local1\s*=\s*(0x[0-9A-Fa-f]+)[\s\n]*\}");

    private static readonly Regex LinuxOsysNear = new(
        @"If\s*\(\s*_OSI\s*\(\s*""Linux""\s*\)\s*\)\s*\{[\s\S]{0,120}?OSYS\s*=\s*(0x[0-9A-Fa-f]+)");

    private static readonly Regex LinuxLocal1Near = new(
        @"If\s*\(\s*_OSI\s*\(\s*""Linux""\s*\)\s*\)\s*\{[\s\S]{0,120}?Local1\s*=\s*(0x[0-9A-Fa-f]+)");

    private static readonly Regex HpOmenHint = new(
        "\"HPQOEM\"|HPIC\\d{4}|\"HP\\s{2,4}\"|OMEN", RegexOptions.IgnoreCase);

    private static readonly Regex Ic04Device = new(
        @"\n(\s*)Scope\s*\(\s*_SB\.PC00\s*\)\s*\n\1\{\s*\n\1\s+Device\s*\(\s*IC04\s*\)\s*\n[\s\S]*?""HPIC0004""[\s\S]*?\n\1\s+\}\s*\n\1\}");

    private static readonly Regex PnotCall = new(@"^(\s*)PNOT\s*\(\s*\)\s*$", RegexOptions.Multiline);

    private static string FormatRevision(long n) => $"0x{n:x8}";

    private static bool DetectHpOmen(string dsl) => DefinitionBlockDsdt.IsMatch(dsl) && HpOmenHint.IsMatch(dsl);

    private static (string Text, string? From, string? To, bool Skipped) BumpDefinitionBlockRevision(string dsl)
    {
        var match = DefinitionBlockDsdt.Match(dsl);
        if (!match.Success) return (dsl, null, null, false);

        var from = match.Groups[1].Value;
        if (LinuxOsysNear.IsMatch(dsl)) return (dsl, from, from, true);

        var current = Convert.ToInt64(from, 16);
        var next = FormatRevision(current + 1);
        var text = DefinitionBlockDsdt.Replace(dsl, m => m.Value.Replace(m.Groups[1].Value, next), 1);
        return (text, from, next, false);
    }

    private sealed record WindowsBlock(int Index, int End, string Indent, int Year, string Value);

    private static List<WindowsBlock> CollectWindowsBlocks(string dsl, Regex re) =>
        re.Matches(dsl)
            .Select(m => new WindowsBlock(m.Index, m.Index + m.Value.Length, m.Groups[1].Value, int.Parse(m.Groups[2].Value), m.Groups[3].Value))
            .ToList();

    private static WindowsBlock? PickNewestBlock(List<WindowsBlock> blocks) =>
        blocks.Count == 0 ? null : blocks.Aggregate((a, b) => b.Year >= a.Year ? b : a);

    private static string BuildLinuxBlock(string indent, string assignment) =>
        $"\n{indent}If (_OSI (\"Linux\"))\n{indent}{{\n{indent}    {assignment}\n{indent}}}";

    private static (string Text, bool Applied, bool Skipped, string? Detail) InsertLinuxMirror(
        string dsl, Regex windowsRe, Regex linuxNearRe, string assignmentKey)
    {
        var blocks = CollectWindowsBlocks(dsl, windowsRe);
        var anchor = PickNewestBlock(blocks);

        if (anchor is null)
        {
            return (dsl, false, false, $"No Windows _OSI block setting {assignmentKey} found.");
        }

        var mirrorDetail = $"{assignmentKey} = {anchor.Value} (mirrors Windows {anchor.Year})";

        var linuxMatch = linuxNearRe.Match(dsl);
        if (linuxMatch.Success && linuxMatch.Groups[1].Value == anchor.Value)
        {
            return (dsl, false, true, $"Already set — {mirrorDetail}.");
        }

        var insert = BuildLinuxBlock(anchor.Indent, $"{assignmentKey} = {anchor.Value}");
        var after = dsl.Substring(anchor.End, Math.Min(500, dsl.Length - anchor.End));
        if (after.Contains("If (_OSI (\"Linux\"))") && after.Contains($"{assignmentKey} = {anchor.Value}"))
        {
            return (dsl, false, true, $"Already present after Windows {anchor.Year}.");
        }

        var text = dsl[..anchor.End] + insert + dsl[anchor.End..];
        return (text, true, false, $"Inserted after Windows {anchor.Year} — {mirrorDetail}.");
    }

    private static (string Text, bool Removed, string? Warning) RemoveHpicDevice(string dsl)
    {
        if (!dsl.Contains("HPIC0004")) return (dsl, false, null);
        if (Ic04Device.IsMatch(dsl)) return (Ic04Device.Replace(dsl, "\n"), true, null);
        return (dsl, false, "HPIC0004 present but IC04 device block layout did not match.");
    }

    private static (string Text, int Count) DisablePnotCalls(string dsl)
    {
        var count = 0;
        var text = PnotCall.Replace(dsl, m =>
        {
            count++;
            return $"{m.Groups[1].Value}//PNOT () // brokn";
        });
        return (text, count);
    }

    /// <summary>Apply standard HP OMEN Linux DSDT patches. Idempotent where possible.</summary>
    public static PatchReport ApplyOmenDsdtPatch(string dsl)
    {
        var steps = new List<PatchStep>();
        var text = dsl;

        var looksLikeHpOmen = DetectHpOmen(text);

        var rev = BumpDefinitionBlockRevision(text);
        text = rev.Text;
        if (rev.From is not null && rev.To is not null)
        {
            steps.Add(rev.Skipped
                ? new PatchStep("revision", PatchStepResult.Skipped, $"Unchanged at {rev.From} (table already patched).")
                : new PatchStep("revision", PatchStepResult.Applied, $"{rev.From} → {rev.To}"));
        }
        else
        {
            steps.Add(new PatchStep("revision", PatchStepResult.Warning, "Could not find a DSDT DefinitionBlock header."));
        }

        var osys = InsertLinuxMirror(text, WindowsOsys, LinuxOsysNear, "OSYS");
        text = osys.Text;
        steps.Add(new PatchStep(
            "linux-osys",
            osys.Applied ? PatchStepResult.Applied : osys.Skipped ? PatchStepResult.Skipped : PatchStepResult.Warning,
            osys.Detail));

        var ic04 = RemoveHpicDevice(text);
        text = ic04.Text;
        steps.Add(new PatchStep(
            "ic04",
            ic04.Removed ? PatchStepResult.Applied : ic04.Warning is not null ? PatchStepResult.Warning : PatchStepResult.Skipped,
            ic04.Removed ? "Removed _SB.PC00.IC04 (HPIC0004)." : ic04.Warning ?? "Not in this DSDT."));

        var pnot = DisablePnotCalls(text);
        text = pnot.Text;
        steps.Add(new PatchStep(
            "pnot",
            pnot.Count > 0 ? PatchStepResult.Applied : PatchStepResult.Skipped,
            pnot.Count > 0 ? $"Commented out {pnot.Count} PNOT() call{(pnot.Count == 1 ? "" : "s")}." : "No PNOT() calls in this DSDT."));

        var local1 = InsertLinuxMirror(text, WindowsLocal1, LinuxLocal1Near, "Local1");
        text = local1.Text;
        steps.Add(new PatchStep(
            "linux-local1",
            local1.Applied ? PatchStepResult.Applied : local1.Skipped ? PatchStepResult.Skipped : PatchStepResult.Warning,
            local1.Detail));

        return new PatchReport(text, steps, looksLikeHpOmen);
    }
}
