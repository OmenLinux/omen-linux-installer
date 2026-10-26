using System.Management;

namespace OmenPrep.Services;

public sealed record HardwareInfo(
    string SystemVendor,
    string ProductName,
    string IdentifyingNumber,
    string BiosVendor,
    string BiosVersion,
    DateTime? BiosReleaseDate);

/// <summary>
/// Reads vendor/model/BIOS identity via WMI (System.Management), so we know
/// exactly which Omen model and firmware revision we're dealing with.
/// </summary>
public static class HardwareDetector
{
    public static HardwareInfo Detect()
    {
        using var productSearcher = new ManagementObjectSearcher(
            "SELECT Vendor, Name, IdentifyingNumber FROM Win32_ComputerSystemProduct");
        using var biosSearcher = new ManagementObjectSearcher(
            "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");

        var product = productSearcher.Get().Cast<ManagementBaseObject>().FirstOrDefault();
        var bios = biosSearcher.Get().Cast<ManagementBaseObject>().FirstOrDefault();

        DateTime? releaseDate = null;
        var rawDate = bios?["ReleaseDate"] as string;
        if (!string.IsNullOrEmpty(rawDate))
        {
            releaseDate = ManagementDateTimeConverter.ToDateTime(rawDate);
        }

        return new HardwareInfo(
            SystemVendor: (product?["Vendor"] as string ?? "").Trim(),
            ProductName: (product?["Name"] as string ?? "").Trim(),
            IdentifyingNumber: (product?["IdentifyingNumber"] as string ?? "").Trim(),
            BiosVendor: (bios?["Manufacturer"] as string ?? "").Trim(),
            BiosVersion: (bios?["SMBIOSBIOSVersion"] as string ?? "").Trim(),
            BiosReleaseDate: releaseDate);
    }
}
