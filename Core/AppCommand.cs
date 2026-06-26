using System.IO;
using System.Runtime.InteropServices;

namespace CopyPasta;

internal static class AppCommand
{
    public const string AddToHistoryOption = "--add-to-history";

    public static string ResolveExecutablePath()
    {
        var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "CopyPasta.exe"
            : "CopyPasta";
        var assemblyDirectory = Path.GetDirectoryName(typeof(AppCommand).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            var assemblySiblingExecutable = Path.Combine(assemblyDirectory, executableName);
            if (File.Exists(assemblySiblingExecutable))
            {
                return assemblySiblingExecutable;
            }
        }

        var bundledExecutable = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(bundledExecutable))
        {
            return bundledExecutable;
        }

        return Environment.ProcessPath ?? bundledExecutable;
    }
}
