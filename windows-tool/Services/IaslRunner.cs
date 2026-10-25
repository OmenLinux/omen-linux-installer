using System.IO;

namespace OmenPrep.Services;

public sealed record IaslLogLine(string Stream, string Text);

public sealed record DisasmResult(string? DslPath, int ExitCode, bool Ok, List<IaslLogLine> Logs);

public sealed record CompileResult(string? AmlPath, int ExitCode, bool Ok, List<IaslLogLine> Logs);

/// <summary>
/// Runs the open-source ACPICA <c>iasl.wasm</c> (same build as omen-web) in-process
/// via Wasmtime — no Node.js, no native iasl.exe on the user's machine.
/// </summary>
public static class IaslRunner
{
    private static readonly object ExtractLock = new();
    private static byte[]? _wasmBytes;

    public static async Task<DisasmResult> DisassembleAsync(
        string dsdtPath, IReadOnlyList<string>? ssdtPaths, string outDir, bool force = false)
    {
        Directory.CreateDirectory(outDir);
        var work = Path.Combine(outDir, ".iasl-work-disasm");
        Directory.CreateDirectory(work);

        var tab = Path.Combine(work, "tab.aml");
        File.Copy(dsdtPath, tab, overwrite: true);

        var argv = new List<string> { "iasl" };
        if (ssdtPaths is not null)
        {
            foreach (var ssdt in ssdtPaths)
            {
                var name = Path.GetFileName(ssdt);
                File.Copy(ssdt, Path.Combine(work, name), overwrite: true);
                argv.Add("-e");
                argv.Add(name);
            }
        }
        argv.Add(force ? "-df" : "-d");
        argv.Add("tab.aml");

        var logs = await Task.Run(() => RunInWorkDir(work, argv.ToArray()));
        var dsl = Directory.GetFiles(work, "*.dsl").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
        string? dslOut = null;
        if (dsl is not null)
        {
            dslOut = Path.Combine(outDir, Path.GetFileName(dsl));
            File.Copy(dsl, dslOut, overwrite: true);
        }

        return new DisasmResult(dslOut, logs.ExitCode, dslOut is not null, logs.Logs.ToList());
    }

    public static async Task<CompileResult> CompileAsync(string dslPath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var work = Path.Combine(outDir, ".iasl-work-compile");
        Directory.CreateDirectory(work);

        var asl = Path.Combine(work, "dsdt.asl");
        File.Copy(dslPath, asl, overwrite: true);

        var logs = await Task.Run(() => RunInWorkDir(work, ["iasl", "dsdt.asl"]));

        var aml = Directory.GetFiles(work, "*.aml")
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
        string? amlOut = null;
        if (aml is not null)
        {
            amlOut = Path.Combine(outDir, "DSDT.aml");
            File.Copy(aml, amlOut, overwrite: true);
        }

        return new CompileResult(amlOut, logs.ExitCode, amlOut is not null, logs.Logs.ToList());
    }

    private static (int ExitCode, IReadOnlyList<IaslLogLine> Logs) RunInWorkDir(string work, string[] argv)
    {
        var wasm = LoadWasm();
        using var host = new IaslWasmHost(work);
        host.Run(wasm, argv);
        return (host.ExitCode, host.Logs);
    }

    private static byte[] LoadWasm()
    {
        if (_wasmBytes is not null) return _wasmBytes;
        lock (ExtractLock)
        {
            if (_wasmBytes is not null) return _wasmBytes;

            var beside = Path.Combine(AppContext.BaseDirectory, "Iasl", "iasl.wasm");
            if (File.Exists(beside))
            {
                _wasmBytes = File.ReadAllBytes(beside);
                return _wasmBytes;
            }

            var asm = typeof(IaslRunner).Assembly;
            using var stream = asm.GetManifestResourceStream("OmenPrep.Iasl.iasl.wasm")
                ?? throw new FileNotFoundException("Embedded iasl.wasm missing.");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            _wasmBytes = ms.ToArray();
            return _wasmBytes;
        }
    }
}
