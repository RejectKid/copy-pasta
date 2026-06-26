using Avalonia;

namespace CopyPasta;

internal static class Program
{
    public static IReadOnlyList<string> StartupHistoryTexts { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        StartupHistoryTexts = StartupArguments.GetHistoryTexts(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}

internal static class StartupArguments
{
    public static IReadOnlyList<string> GetHistoryTexts(string[] args)
    {
        var historyTexts = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], AppCommand.AddToHistoryOption, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                var text = args[++i];
                if (!string.IsNullOrWhiteSpace(text))
                {
                    historyTexts.Add(text);
                }
            }
        }

        return historyTexts;
    }
}
