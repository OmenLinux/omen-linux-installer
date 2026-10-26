using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using OmenPrep.Services;

namespace OmenPrep;

public partial class MainWindow : Window
{
    private string? _dsdtPath;
    private readonly List<string> _ssdtPaths = [];
    private byte[]? _patchedAml;
    private string? _lastOutputPath;

    public MainWindow()
    {
        InitializeComponent();
        DumpPathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "omen-prep-dump");
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy) => BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

    // ---- One-click flow -------------------------------------------------

    private async void OnOneClickClick(object sender, RoutedEventArgs e)
    {
        OneClickButton.IsEnabled = false;
        QuickProgressPanel.Visibility = Visibility.Visible;
        QuickProgressList.Items.Clear();
        QuickResultActions.Visibility = Visibility.Collapsed;
        SetBusy(true);

        var detectRow = AddQuickRow(RowStatus.Info, "Detecting your Omen…");
        try
        {
            var info = await Task.Run(HardwareDetector.Detect);
            ApplyDetectResult(info);
            ReplaceQuickRow(detectRow, RowStatus.Ok, "Detected hardware", $"{info.SystemVendor} {info.ProductName} — BIOS {info.BiosVersion}");

            var dumpRow = AddQuickRow(RowStatus.Info, "Dumping ACPI tables…");
            var dumpDir = Path.Combine(Path.GetTempPath(), "omen-prep-dump-" + DateTime.Now.Ticks);
            var dumpOk = await RunDumpAsync(dumpDir, reportToDumpList: false);
            if (!dumpOk)
            {
                ReplaceQuickRow(dumpRow, RowStatus.Error, "Dump failed", "No ACPI tables were found on this machine.");
                return;
            }
            ReplaceQuickRow(dumpRow, RowStatus.Ok, "Dumped ACPI tables", $"Saved to {dumpDir}");

            var patchRow = AddQuickRow(RowStatus.Info, "Patching DSDT…");
            var patchOk = await RunPatchAsync(reportToPatchList: false);
            if (!patchOk)
            {
                ReplaceQuickRow(patchRow, RowStatus.Error, "Patch failed", StatusText.Text);
                return;
            }
            ReplaceQuickRow(patchRow, RowStatus.Ok, "Patched DSDT", $"{_patchedAml!.Length:N0} bytes, ready to install");

            SetBusy(false);
            var isoRow = AddQuickRow(RowStatus.Info, "Pick the Linux ISO to fix…");
            var openDialog = new OpenFileDialog { Title = "Select your Linux install ISO", Filter = "ISO image (*.iso)|*.iso" };
            if (openDialog.ShowDialog() != true)
            {
                ReplaceQuickRow(isoRow, RowStatus.Warn, "Cancelled", "No ISO was selected.");
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                Title = "Save the fixed ISO as…",
                FileName = Path.GetFileNameWithoutExtension(openDialog.FileName) + "-omen.iso",
                Filter = "ISO image (*.iso)|*.iso",
            };
            if (saveDialog.ShowDialog() != true)
            {
                ReplaceQuickRow(isoRow, RowStatus.Warn, "Cancelled", "No save location was chosen.");
                return;
            }
            ReplaceQuickRow(isoRow, RowStatus.Ok, "ISO selected", Path.GetFileName(openDialog.FileName));

            SetBusy(true);
            var injectRow = AddQuickRow(RowStatus.Info, "Injecting the fix into the ISO…");
            try
            {
                var tables = new Dictionary<string, byte[]> { ["dsdt.aml"] = _patchedAml! };
                var result = await IsoAcpiInjector.InjectAsync(openDialog.FileName, saveDialog.FileName, tables);
                _lastOutputPath = result.OutputIsoPath;
                ReplaceQuickRow(injectRow, RowStatus.Ok, "Done!",
                    $"Patched {result.FoundPath} → {Path.GetFileName(result.OutputIsoPath)}. " +
                    "Live: sudo bash /cdrom/apply-omen-acpi.sh /mnt — after reboot: sudo bash /cdrom/post-install-omen.sh");
                QuickResultActions.Visibility = Visibility.Visible;
                StatusText.Text =
                    "Ready to flash. After install: apply-omen-acpi.sh /mnt (still live), reboot, " +
                    "then sudo bash /cdrom/post-install-omen.sh";
            }
            catch (Exception ex)
            {
                ReplaceQuickRow(injectRow, RowStatus.Error, "Injection failed", ex.Message);
            }
        }
        finally
        {
            SetBusy(false);
            OneClickButton.IsEnabled = true;
        }
    }

    private ResultRow AddQuickRow(RowStatus status, string title, string? detail = null)
    {
        var row = new ResultRow(status, title, detail);
        QuickProgressList.Items.Add(row);
        return row;
    }

    private void ReplaceQuickRow(ResultRow oldRow, RowStatus status, string title, string? detail)
    {
        var index = QuickProgressList.Items.IndexOf(oldRow);
        if (index >= 0) QuickProgressList.Items[index] = new ResultRow(status, title, detail);
    }

    private void OnShowResultInFolderClick(object sender, RoutedEventArgs e)
    {
        if (_lastOutputPath is null) return;
        Process.Start("explorer.exe", $"/select,\"{_lastOutputPath}\"");
    }

    // ---- Manual steps (Advanced) -----------------------------------------

    private void ApplyDetectResult(HardwareInfo info)
    {
        ModelText.Text = $"{info.SystemVendor} {info.ProductName}".Trim();
        SerialText.Text = info.IdentifyingNumber;
        BiosText.Text = $"{info.BiosVendor} {info.BiosVersion}".Trim();
        BiosDateText.Text = info.BiosReleaseDate?.ToString("yyyy-MM-dd") ?? "—";
    }

    private async void OnDetectClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Detecting…";
        SetBusy(true);
        try
        {
            var info = await Task.Run(HardwareDetector.Detect);
            ApplyDetectResult(info);
            StatusText.Text = "Detected.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Detect failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnBrowseDumpClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where to save the ACPI dump",
            InitialDirectory = DumpPathBox.Text,
        };
        if (dialog.ShowDialog() == true)
        {
            DumpPathBox.Text = dialog.FolderName;
        }
    }

    /// <summary>Dumps ACPI tables to <paramref name="outputDir"/>, setting _dsdtPath/_ssdtPaths. Returns false if nothing was found.</summary>
    private async Task<bool> RunDumpAsync(string outputDir, bool reportToDumpList)
    {
        if (reportToDumpList) DumpResultsList.Items.Clear();
        _dsdtPath = null;
        _ssdtPaths.Clear();

        var tables = await Task.Run(() => AcpiDumper.Dump(outputDir));
        if (tables.Count == 0) return false;

        var info = await Task.Run(HardwareDetector.Detect);
        File.WriteAllText(
            Path.Combine(outputDir, "dump-manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(new { system = info, tables },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        foreach (var t in tables)
        {
            if (reportToDumpList)
            {
                DumpResultsList.Items.Add(new ResultRow(RowStatus.Ok, t.FileName, $"{t.Signature}, {t.Length:N0} bytes"));
            }
            var fullPath = Path.Combine(outputDir, t.FileName);
            if (t.TableType == "DSDT" && _dsdtPath is null) _dsdtPath = fullPath;
            else if (t.TableType == "SSDT") _ssdtPaths.Add(fullPath);
        }

        return _dsdtPath is not null;
    }

    private async void OnDumpClick(object sender, RoutedEventArgs e)
    {
        var outputDir = DumpPathBox.Text;
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            StatusText.Text = "Choose an output folder first.";
            return;
        }

        StatusText.Text = "Dumping ACPI tables…";
        SetBusy(true);
        try
        {
            var ok = await RunDumpAsync(outputDir, reportToDumpList: true);
            StatusText.Text = ok
                ? $"Dumped ACPI tables to {outputDir}. Ready to patch."
                : "No ACPI tables found under HKLM\\HARDWARE\\ACPI.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Dump failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Disassembles _dsdtPath, applies the Omen patch rules, and recompiles. Sets _patchedAml on success.</summary>
    private async Task<bool> RunPatchAsync(bool reportToPatchList)
    {
        if (_dsdtPath is null)
        {
            StatusText.Text = "Dump the ACPI tables first.";
            return false;
        }

        if (reportToPatchList) PatchStepsList.Items.Clear();
        _patchedAml = null;

        var workDir = Path.Combine(Path.GetTempPath(), "omen-prep-patch-" + Guid.NewGuid());

        var disasm = await IaslRunner.DisassembleAsync(_dsdtPath, _ssdtPaths, workDir);
        if (!disasm.Ok || disasm.DslPath is null)
        {
            StatusText.Text = $"Disassembly failed (exit {disasm.ExitCode}). See {workDir}.";
            return false;
        }

        var dsl = await File.ReadAllTextAsync(disasm.DslPath);
        var report = OmenDsdtPatch.ApplyOmenDsdtPatch(dsl);

        if (reportToPatchList)
        {
            foreach (var step in report.Steps)
            {
                var (title, _) = OmenDsdtPatch.Checklist[step.Id];
                var status = step.Result switch
                {
                    OmenDsdtPatch.PatchStepResult.Applied => RowStatus.Ok,
                    OmenDsdtPatch.PatchStepResult.Skipped => RowStatus.Info,
                    _ => RowStatus.Warn,
                };
                PatchStepsList.Items.Add(new ResultRow(status, title, step.Detail));
            }

            if (!report.LooksLikeHpOmen)
            {
                PatchStepsList.Items.Add(new ResultRow(RowStatus.Warn, "Doesn't look like an HP OMEN DSDT",
                    "Patches were still applied — double-check the result."));
            }
        }

        var patchedDslPath = Path.Combine(workDir, "patched.dsl");
        await File.WriteAllTextAsync(patchedDslPath, report.Dsl);

        var compile = await IaslRunner.CompileAsync(patchedDslPath, workDir);
        if (!compile.Ok || compile.AmlPath is null)
        {
            StatusText.Text = $"Compile failed (exit {compile.ExitCode}). See {workDir}.";
            return false;
        }

        _patchedAml = await File.ReadAllBytesAsync(compile.AmlPath);
        return true;
    }

    private async void OnPatchClick(object sender, RoutedEventArgs e)
    {
        SavePatchedButton.IsEnabled = false;
        InjectButton.IsEnabled = false;
        StatusText.Text = "Disassembling…";
        SetBusy(true);
        try
        {
            var ok = await RunPatchAsync(reportToPatchList: true);
            if (ok)
            {
                SavePatchedButton.IsEnabled = true;
                InjectButton.IsEnabled = SourceIsoBox.Text.Length > 0;
                StatusText.Text = $"Patched DSDT.aml ready ({_patchedAml!.Length:N0} bytes).";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Patch failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnSavePatchedClick(object sender, RoutedEventArgs e)
    {
        if (_patchedAml is null) return;
        var dialog = new SaveFileDialog { FileName = "DSDT.aml", Filter = "ACPI table (*.aml)|*.aml" };
        if (dialog.ShowDialog() == true)
        {
            File.WriteAllBytes(dialog.FileName, _patchedAml);
            StatusText.Text = $"Saved {dialog.FileName}.";
        }
    }

    private void OnLoadAmlClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ACPI table (*.aml)|*.aml" };
        if (dialog.ShowDialog() != true) return;

        _patchedAml = File.ReadAllBytes(dialog.FileName);
        SavePatchedButton.IsEnabled = true;
        InjectButton.IsEnabled = SourceIsoBox.Text.Length > 0;
        StatusText.Text = $"Loaded {Path.GetFileName(dialog.FileName)} ({_patchedAml.Length:N0} bytes) — ready to inject.";
    }

    private void OnChooseIsoClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ISO image (*.iso)|*.iso" };
        if (dialog.ShowDialog() == true)
        {
            SourceIsoBox.Text = dialog.FileName;
            InjectButton.IsEnabled = _patchedAml is not null;
        }
    }

    private async void OnInjectClick(object sender, RoutedEventArgs e)
    {
        if (_patchedAml is null || string.IsNullOrEmpty(SourceIsoBox.Text)) return;

        var defaultName = Path.GetFileNameWithoutExtension(SourceIsoBox.Text) + "-omen.iso";
        var dialog = new SaveFileDialog { FileName = defaultName, Filter = "ISO image (*.iso)|*.iso" };
        if (dialog.ShowDialog() != true) return;

        StatusText.Text = "Finding the initramfs and injecting the patched ACPI table…";
        InjectButton.IsEnabled = false;
        SetBusy(true);
        try
        {
            var tables = new Dictionary<string, byte[]> { ["dsdt.aml"] = _patchedAml };
            var result = await IsoAcpiInjector.InjectAsync(SourceIsoBox.Text, dialog.FileName, tables);
            _lastOutputPath = result.OutputIsoPath;
            StatusText.Text =
                $"Done — patched {result.FoundPath} (+{result.InjectedBytes:N0} bytes). " +
                $"Saved to {result.OutputIsoPath}. After reboot into Linux: sudo bash /cdrom/post-install-omen.sh";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Injection failed: {ex.Message}";
        }
        finally
        {
            InjectButton.IsEnabled = true;
            SetBusy(false);
        }
    }
}
