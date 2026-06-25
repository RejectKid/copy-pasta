namespace CopyPasta;

internal sealed class TextStyleSnapshot
{
    public string FontName { get; set; } = string.Empty;
    public double? FontSize { get; set; }
    public int? FontWeight { get; set; }
    public bool? IsItalic { get; set; }
    public int? ForegroundColor { get; set; }
    public int? BackgroundColor { get; set; }

    public bool HasAny =>
        !string.IsNullOrEmpty(FontName) ||
        FontSize.HasValue ||
        FontWeight.HasValue ||
        IsItalic.HasValue ||
        ForegroundColor.HasValue ||
        BackgroundColor.HasValue;
}
