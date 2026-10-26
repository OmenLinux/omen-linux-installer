namespace OmenPrep;

public enum RowStatus { Ok, Warn, Error, Info }

/// <summary>View-model for one row in the dump/patch result lists.</summary>
public sealed record ResultRow(RowStatus Status, string Title, string? Detail)
{
    public string Glyph => Status switch
    {
        RowStatus.Ok => "",    // CheckMark
        RowStatus.Warn => "",  // Warning
        RowStatus.Error => "", // Cancel
        _ => "",               // Info
    };

    public string GlyphColor => Status switch
    {
        RowStatus.Ok => "#4CC38A",
        RowStatus.Warn => "#E2A32B",
        RowStatus.Error => "#E2231A",
        _ => "#6B7280",
    };
}
