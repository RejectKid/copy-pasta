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
    private const string AddToHistoryOption = "--add-to-history";

    public static IReadOnlyList<string> GetHistoryTexts(string[] args)
    {
        var historyTexts = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], AddToHistoryOption, StringComparison.OrdinalIgnoreCase) ||
                i + 1 >= args.Length)
            {
                continue;
            }

            var text = args[++i];
            if (!string.IsNullOrWhiteSpace(text))
            {
                historyTexts.Add(text);
            }
        }

        return historyTexts;
    }
}
