namespace KSLauncher.App.Services;

public static class JvmTuning
{
    public static int RecommendedHeapMb
    {
        get
        {
            var available = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
            // Keep at least 2 GB for Windows, drivers, and background applications.
            return Math.Clamp((available - 2048) / 2, 2048, 8192);
        }
    }

    public static string HardwareSummary => $"{Environment.ProcessorCount} логических потоков";
}
