using OmenPrep.Services;
using static OmenPrep.Services.OmenDsdtPatch;

namespace OmenPrep.Tests;

public class OmenDsdtPatchTests
{
    private const string SyntheticDsl = """
        DefinitionBlock ("", "DSDT", 2, "HPQOEM", "0000", 0x00000002)
        {
            Scope (_SB)
            {
                If (_OSI ("Windows 2015"))
                {
                    OSYS = 0x07DF
                }
                If (_OSI ("Windows 2021"))
                {
                    OSYS = 0x0816
                }
                If (_OSI ("Windows 2015"))
                {
                    Local1 = 0x07DF
                }
                If (_OSI ("Windows 2021"))
                {
                    Local1 = 0x0816
                }
            }

            Scope (_SB.PC00)
            {
                Device (IC04)
                {
                    Name (_HID, "HPIC0004")
                    Name (_UID, Zero)
                }
            }

            Method (_INI, 0, NotSerialized)
            {
                PNOT ()
            }
        }
        """;

    [Fact]
    public void AppliesAllFiveStandardPatches()
    {
        var report = ApplyOmenDsdtPatch(SyntheticDsl);

        Assert.Equal(5, report.Steps.Count);
        Assert.All(report.Steps, s => Assert.Equal(PatchStepResult.Applied, s.Result));

        var revision = report.Steps.Single(s => s.Id == "revision");
        Assert.Contains("0x00000002 → 0x00000003", revision.Detail);

        Assert.Contains("DefinitionBlock (\"\", \"DSDT\", 2, \"HPQOEM\", \"0000\", 0x00000003)", report.Dsl);
        Assert.Contains("HPIC0004", SyntheticDsl); // sanity: input really had it
        Assert.DoesNotContain("HPIC0004", report.Dsl); // ...and the patched output removed it
        Assert.Contains("//PNOT () // brokn", report.Dsl);
    }

    [Fact]
    public void MirrorsNewestWindowsBlock_NotOldest()
    {
        var report = ApplyOmenDsdtPatch(SyntheticDsl);

        // 2021 (0x0816) is newer than 2015 (0x07DF) - the Linux mirrors must use 0x0816.
        var osysStep = report.Steps.Single(s => s.Id == "linux-osys");
        Assert.Contains("0x0816", osysStep.Detail);
        Assert.Contains("Windows 2021", osysStep.Detail);

        var local1Step = report.Steps.Single(s => s.Id == "linux-local1");
        Assert.Contains("0x0816", local1Step.Detail);
    }

    [Fact]
    public void SecondPassIsIdempotent()
    {
        var first = ApplyOmenDsdtPatch(SyntheticDsl);
        var second = ApplyOmenDsdtPatch(first.Dsl);

        Assert.All(second.Steps, s => Assert.NotEqual(PatchStepResult.Applied, s.Result));
    }

    [Fact]
    public void DetectsHpOmenFromDsl()
    {
        var report = ApplyOmenDsdtPatch(SyntheticDsl);
        Assert.True(report.LooksLikeHpOmen);
    }

    [Fact]
    public void NonOmenDslIsNotFlaggedAsHpOmen()
    {
        var plain = """
            DefinitionBlock ("", "DSDT", 2, "ACME01", "0000", 0x00000001)
            {
                Scope (_SB) {}
            }
            """;
        var report = ApplyOmenDsdtPatch(plain);
        Assert.False(report.LooksLikeHpOmen);
    }
}
