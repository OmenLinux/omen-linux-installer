using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OmenPrep.Services;

namespace OmenPrep.Tests;

/// <summary>
/// Golden fixtures from
/// https://github.com/OmenLinux/ACPI-Fix-for-HP-Omen-16-u0000sl (F35).
/// Ensures our live patcher matches the known-good published DSDT.
/// </summary>
public class GoldenOmenDsdtTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Omen16-u0000sl", name);

    private static string? HeaderRev(string dsl)
    {
        var m = Regex.Match(
            dsl,
            @"DefinitionBlock\s*\(\s*""[^""]*""\s*,\s*""DSDT""\s*,\s*\d+\s*,\s*""[^""]*""\s*,\s*""[^""]*""\s*,\s*(0x[0-9A-Fa-f]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    [Fact]
    public async Task Patcher_OnPublishedOriginal_MatchesGoldenSemantics()
    {
        var originalAml = Fixture("original.aml");
        var goldenAml = Fixture("golden-patched.aml");
        Assert.True(File.Exists(originalAml), $"Missing fixture {originalAml}");
        Assert.True(File.Exists(goldenAml), $"Missing fixture {goldenAml}");

        var work = Path.Combine(Path.GetTempPath(), "omen-golden-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(work);
        try
        {
            var disasm = await IaslRunner.DisassembleAsync(originalAml, ssdtPaths: null, work, force: true);
            Assert.True(disasm.Ok, string.Join("\n", disasm.Logs.Select(l => l.Text)));
            Assert.NotNull(disasm.DslPath);
            var originalDsl = await File.ReadAllTextAsync(disasm.DslPath!);

            var report = OmenDsdtPatch.ApplyOmenDsdtPatch(originalDsl);
            Assert.Contains(report.Steps, s => s.Id == "revision" && s.Result == OmenDsdtPatch.PatchStepResult.Applied);
            Assert.DoesNotContain("HPIC0004", report.Dsl);
            Assert.Contains("_OSI (\"Linux\")", report.Dsl);
            Assert.Contains("//PNOT () // brokn", report.Dsl);

            var goldWork = Path.Combine(work, "gold");
            Directory.CreateDirectory(goldWork);
            var goldDisasm = await IaslRunner.DisassembleAsync(goldenAml, ssdtPaths: null, goldWork, force: true);
            Assert.True(goldDisasm.Ok, string.Join("\n", goldDisasm.Logs.Select(l => l.Text)));
            Assert.NotNull(goldDisasm.DslPath);
            var goldDsl = await File.ReadAllTextAsync(goldDisasm.DslPath!);

            Assert.DoesNotContain("HPIC0004", goldDsl);
            Assert.Contains("_OSI (\"Linux\")", goldDsl);

            var origRev = HeaderRev(originalDsl);
            var oursRev = HeaderRev(report.Dsl);
            var goldRev = HeaderRev(goldDsl);
            Assert.NotNull(origRev);
            Assert.NotNull(oursRev);
            Assert.NotNull(goldRev);
            Assert.NotEqual(origRev, oursRev);
            Assert.NotEqual(origRev, goldRev);

            var patchedDslPath = Path.Combine(work, "patched.dsl");
            await File.WriteAllTextAsync(patchedDslPath, report.Dsl, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var compile = await IaslRunner.CompileAsync(patchedDslPath, work);
            Assert.True(compile.Ok, string.Join("\n", compile.Logs.Select(l => l.Text)));
            Assert.NotNull(compile.AmlPath);
            var ourAml = await File.ReadAllBytesAsync(compile.AmlPath!);
            Assert.True(ourAml.Length > 1000);
            Assert.Equal("DSDT"u8.ToArray(), ourAml.AsSpan(0, 4).ToArray());

            var goldenBytes = await File.ReadAllBytesAsync(goldenAml);
            Assert.InRange(ourAml.Length, goldenBytes.Length - 64_000, goldenBytes.Length + 64_000);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task Inject_GoldenPatchedAml_IsVisibleViaVerify()
    {
        var goldenAml = await File.ReadAllBytesAsync(Fixture("golden-patched.aml"));
        var dir = Path.Combine(Path.GetTempPath(), "omen-golden-iso-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var builder = new DiscUtils.Iso9660.CDBuilder { UseJoliet = true, VolumeIdentifier = "TEST" };
            builder.AddDirectory("CASPER");
            builder.AddFile("CASPER\\INITRD", new byte[] { 0x1F, 0x8B, 0x08, 0x00, 1, 2, 3 });
            var source = Path.Combine(dir, "source.iso");
            builder.Build(source);

            var dest = Path.Combine(dir, "out.iso");
            await IsoAcpiInjector.InjectAsync(source, dest,
                new Dictionary<string, byte[]> { ["dsdt.aml"] = goldenAml });

            var verified = await IsoAcpiInjector.VerifyAsync(dest);
            var table = Assert.Single(verified.Tables);
            Assert.Equal(goldenAml.Length, table.Length);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(goldenAml)).ToLowerInvariant(),
                table.Sha256);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }
}
