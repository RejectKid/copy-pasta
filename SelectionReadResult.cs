namespace CopyPasta;

internal sealed record SelectionReadResult(string Text, string Source, TextStyleSnapshot? Style = null);
