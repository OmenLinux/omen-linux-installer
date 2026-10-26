using System.Security.Cryptography;
using OmenPrep.Services;

namespace OmenPrep.Tests;

/// <summary>
/// Rebuilds <c>our-patched.aml</c> from the GitHub original via omen-prep's
/// iasl + patch rules, and checks it stays close to the published golden.
/// </summary>
public class EmitOurPatchedTests
{
    [Fact]
    public async Task RebuildOurPatchedAml_MatchesGoldenBallpark()
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Omen16-u0000sl");
        var orig = Path.Combine(fixtures, "original.aml");
        var golden = Path.Combine(fixtures, "golden-patched.aml");
        var srcFixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "Omen16-u0000sl"));

        var work = Path.Combine(Path.GetTempPath(), "emit-ours-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(work);
        try
        {
            var disasm = await IaslRunner.DisassembleAsync(orig, null, work, force: true);
            Assert.True(disasm.Ok, string.Join("\n", disasm.Logs.Select(l => l.Text)));
            var report = OmenDsdtPatch.ApplyOmenDsdtPatch(await File.ReadAllTextAsync(disasm.DslPath!));
            var dsl = Path.Combine(work, "patched.dsl");
            await File.WriteAllTextAsync(dsl, report.Dsl);
            var compile = await IaslRunner.CompileAsync(dsl, work);
            Assert.True(compile.Ok, string.Join("\n", compile.Logs.Select(l => l.Text)));

            var ours = await File.ReadAllBytesAsync(compile.AmlPath!);
            var gold = await File.ReadAllBytesAsync(golden);

            Directory.CreateDirectory(srcFixtures);
            var outPath = Path.Combine(srcFixtures, "our-patched.aml");
            await File.WriteAllBytesAsync(outPath, ours);
            await File.WriteAllBytesAsync(Path.Combine(fixtures, "our-patched.aml"), ours);

            Assert.Equal("DSDT"u8.ToArray(), ours.AsSpan(0, 4).ToArray());
            Assert.InRange(ours.Length, gold.Length - 8_000, gold.Length + 8_000);
            // Not byte-identical (iasl/comments), but must not be the untouched original.
            Assert.NotEqual(
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(orig))),
                Convert.ToHexString(SHA256.HashData(ours)));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* temp */ }
        }
    }
}
