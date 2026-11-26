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

    public static string HardwareSummary => $"{Environment.ProcessorCount} логических потоков CPU (все ядра активны)";

    /// <summary>
    /// Оптимизированные аргументы JVM для использования всех ядер процессора, 
    /// быстрой прогрузки чанков и мгновенной генерации мира без фризов.
    /// </summary>
    public static List<string> GetOptimizedJvmArguments(bool stringDeduplication)
    {
        int cores = Environment.ProcessorCount;
        int gcThreads = Math.Clamp(cores / 2, 2, 8);
        int concThreads = Math.Max(1, gcThreads / 2);

        var args = new List<string>
        {
            // 1. Сбалансированные потоки GC без перегрузки рендер-потоков Minecraft
            $"-XX:ParallelGCThreads={gcThreads}",
            $"-XX:ConcGCThreads={concThreads}",

            // 2. Высокопроизводительный G1GC с минимальными задержками (Aikar + modern Minecraft tuning)
            "-XX:+UnlockExperimentalVMOptions",
            "-XX:+UseG1GC",
            "-XX:+ParallelRefProcEnabled",
            "-XX:MaxGCPauseMillis=25",
            "-XX:+DisableExplicitGC",
            "-XX:+AlwaysPreTouch",
            "-XX:G1NewSizePercent=20",
            "-XX:G1MaxNewSizePercent=40",
            "-XX:G1HeapRegionSize=16M",
            "-XX:G1ReservePercent=15",
            "-XX:InitiatingHeapOccupancyPercent=20",
            "-XX:G1MixedGCCountTarget=4",
            "-XX:SurvivorRatio=32",
            "-XX:MaxTenuringThreshold=1",
            "-XX:+PerfDisableSharedMem",
            "-XX:+OptimizeStringConcat",
            "-Djava.net.preferIPv4Stack=true",
            "-Dsun.rmi.dgc.server.gcInterval=2147483646"
        };

        if (stringDeduplication)
        {
            args.Add("-XX:+UseStringDeduplication");
        }

        return args;
    }
}
