namespace CopyPasta;

internal sealed class HistoryEntry
{
    public string Text { get; set; } = string.Empty;
    public TextStyleSnapshot? Style { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;

    public bool HasContent => !string.IsNullOrEmpty(Text);
    public bool HasStyle => Style is { HasAny: true };

    public string DisplayText
    {
        get
        {
            var singleLine = Text
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Replace("\t", " ", StringComparison.Ordinal)
                .Trim();

            if (singleLine.Length == 0)
            {
                singleLine = "(whitespace)";
            }

            if (singleLine.Length > 76)
            {
                singleLine = singleLine[..73] + "...";
            }

            var kind = HasStyle ? "[Rich text] " : string.Empty;
            return $"{CapturedAt.LocalDateTime:g}  {kind}{singleLine}";
        }
    }

    public override string ToString() => DisplayText;
}
