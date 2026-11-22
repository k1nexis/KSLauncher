using System.Diagnostics;
using System.IO;

namespace KSLauncher.App.Services;

public sealed record JavaRuntimeInfo(
    string Name,
    string Path,
    int MajorVersion,
    bool IsAuto = false
);

public sealed class JavaService
{
    public IReadOnlyList<JavaRuntimeInfo> DetectInstalledJavas()
    {
        var list = new List<JavaRuntimeInfo>
        {
            new("Автоматический выбор (рекомендуется)", "", 0, IsAuto: true)
        };

        var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Check JAVA_HOME
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var javaw = Path.Combine(javaHome, "bin", "javaw.exe");
            if (File.Exists(javaw) && discoveredPaths.Add(javaw))
            {
                var ver = ProbeJavaVersion(javaw);
                list.Add(new JavaRuntimeInfo($"JAVA_HOME (Java {ver})", javaw, ver));
            }
        }

        // 2. Scan standard directories
        var scanFolders = new[]
        {
            @"C:\Program Files\Java",
            @"C:\Program Files\Eclipse Adoptium",
            @"C:\Program Files\Microsoft",
            @"C:\Program Files\BellSoft",
            @"C:\Program Files\Amazon Corretto",
            @"C:\Program Files\Zulu",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Eclipse Adoptium")
        };

        foreach (var baseDir in scanFolders)
        {
            if (!Directory.Exists(baseDir)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(baseDir))
                {
                    var javaw = Path.Combine(dir, "bin", "javaw.exe");
                    if (File.Exists(javaw) && discoveredPaths.Add(javaw))
                    {
                        var ver = ProbeJavaVersion(javaw);
                        var dirName = Path.GetFileName(dir);
                        list.Add(new JavaRuntimeInfo($"{dirName} (Java {ver})", javaw, ver));
                    }
                }
            }
            catch { }
        }

        return list;
    }

    public static int ProbeJavaVersion(string javawPath)
    {
        try
        {
            var javaExe = javawPath.Replace("javaw.exe", "java.exe", StringComparison.OrdinalIgnoreCase);
            if (!File.Exists(javaExe)) javaExe = javawPath;

            var startInfo = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-version",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                var output = process.StandardError.ReadToEnd();
                process.WaitForExit(1500);

                // Check version strings like "1.8.0_...", "17.0.2", "21.0.1"
                var match = System.Text.RegularExpressions.Regex.Match(output, @"version\s+""(1\.)?(\d+)");
                if (match.Success && int.TryParse(match.Groups[2].Value, out var major))
                {
                    return major;
                }
            }
        }
        catch { }

        return 17; // Safe fallback
    }
}
