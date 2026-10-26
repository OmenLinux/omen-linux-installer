using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmenPrep.Services;

public sealed record IaslLogLine(
    [property: JsonPropertyName("stream")] string Stream,
    [property: JsonPropertyName("text")] string Text);

public sealed record DisasmResult(string? DslPath, int ExitCode, bool Ok, List<IaslLogLine> Logs);

public sealed record CompileResult(string? AmlPath, int ExitCode, bool Ok, List<IaslLogLine> Logs);

/// <summary>
/// Shells out to Node to run the same iasl.js/iasl.wasm build omen-web uses
/// in the browser (see Iasl/iasl-runner.js). Requires Node.js on PATH;
/// deliberately avoids sourcing a separate native iasl.exe so the exact
/// ACPICA build already shipped and tested in omen-web is what runs here
/// too.
/// </summary>
public static class IaslRunner
{
    private static string RunnerDir => Path.Combine(AppContext.BaseDirectory, "Iasl");
    private static string RunnerScript => Path.Combine(RunnerDir, "iasl-runner.js");

    private sealed record RawResult(
        [property: JsonPropertyName("exitCode")] int ExitCode,
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("dslName")] string? DslName,
        [property: JsonPropertyName("logs")] List<IaslLogLine> Logs);

    public static async Task<DisasmResult> DisassembleAsync(
        string dsdtPath, IReadOnlyList<string>? ssdtPaths, string outDir, bool force = false)
    {
        var args = new List<string> { RunnerScript, "disasm", "--dsdt", dsdtPath };
        foreach (var s in ssdtPaths ?? Array.Empty<string>())
        {
            args.Add("--ssdt");
            args.Add(s);
        }
        args.Add("--out");
        args.Add(outDir);
        if (force) args.Add("--force");

        await RunNodeAsync(args);
        var raw = await ReadResultAsync(outDir);
        var dslPath = raw.Ok && raw.DslName is not null ? Path.Combine(outDir, raw.DslName) : null;
        return new DisasmResult(dslPath, raw.ExitCode, raw.Ok, raw.Logs);
    }

    public static async Task<CompileResult> CompileAsync(string dslPath, string outDir)
    {
        var args = new List<string> { RunnerScript, "compile", "--dsl", dslPath, "--out", outDir };
        await RunNodeAsync(args);
        var raw = await ReadResultAsync(outDir);
        var amlPath = raw.Ok ? Path.Combine(outDir, "DSDT.aml") : null;
        return new CompileResult(amlPath, raw.ExitCode, raw.Ok, raw.Logs);
    }

    private static async Task RunNodeAsync(List<string> args)
    {
        var psi = new ProcessStartInfo("node")
        {
            WorkingDirectory = RunnerDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start node. Is Node.js installed and on PATH?");

        await process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
    }

    private static async Task<RawResult> ReadResultAsync(string outDir)
    {
        var resultPath = Path.Combine(outDir, "result.json");
        if (!File.Exists(resultPath))
        {
            return new RawResult(-1, false, null, [new IaslLogLine("stderr", "iasl-runner produced no result.json.")]);
        }
        var json = await File.ReadAllTextAsync(resultPath);
        return JsonSerializer.Deserialize<RawResult>(json)
            ?? new RawResult(-1, false, null, [new IaslLogLine("stderr", "Could not parse result.json.")]);
    }
}
