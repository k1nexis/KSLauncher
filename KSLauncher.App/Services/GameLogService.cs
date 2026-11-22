using System.Diagnostics;
using System.Text;

namespace KSLauncher.App.Services;

public sealed class GameLogService
{
    private readonly List<string> _logs = new();
    private readonly object _lock = new();
    public const int MaxLines = 1500;

    public event Action<string, bool>? MessageReceived;

    public IReadOnlyList<string> GetLogs()
    {
        lock (_lock) { return _logs.ToArray(); }
    }

    public void Clear()
    {
        lock (_lock) { _logs.Clear(); }
    }

    public void Append(string line, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_lock)
        {
            if (_logs.Count >= MaxLines) _logs.RemoveAt(0);
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var formatted = $"[{timestamp}] {(isError ? "[ERROR] " : "")}{line}";
            _logs.Add(formatted);
        }
        MessageReceived?.Invoke(line, isError);
    }

    public void AttachToProcess(Process process, Action<int>? onExited = null)
    {
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) Append(e.Data, isError: false);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) Append(e.Data, isError: true);
        };

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch { }

        process.Exited += (_, _) =>
        {
            var code = process.ExitCode;
            Append($"Процесс Minecraft завершился с кодом {code}.", isError: code != 0);
            onExited?.Invoke(code);
        };
    }

    public string GetFullLogText()
    {
        lock (_lock) { return string.Join(Environment.NewLine, _logs); }
    }
}
