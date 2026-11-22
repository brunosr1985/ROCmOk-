using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace RocmAudioSplitter.Core.Audio;

public sealed record ActiveAudioProcessInfo(
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    string DisplayLabel);

public static class AudioProcessEnumerator
{
    public static IReadOnlyList<ActiveAudioProcessInfo> GetActiveAudioProcesses()
    {
        var results = new Dictionary<int, ActiveAudioProcessInfo>();
        int currentPid = Environment.ProcessId;

        using var enumerator = new MMDeviceEnumerator();
        MMDevice defaultRender;
        try
        {
            defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            return Array.Empty<ActiveAudioProcessInfo>();
        }

        using (defaultRender)
        {
            var sessionManager = defaultRender.AudioSessionManager;
            var sessions = sessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                int pid = (int)session.GetProcessID;
                if (pid <= 0 || pid == currentPid || results.ContainsKey(pid))
                    continue;

                try
                {
                    using var proc = Process.GetProcessById(pid);
                    string procName = proc.ProcessName;
                    string title = string.IsNullOrWhiteSpace(proc.MainWindowTitle)
                        ? procName
                        : proc.MainWindowTitle;

                    // Para Chrome/Edge/Spotify, tenta encontrar o processo pai com janela principal
                    string label = title != procName
                        ? $"{procName} ({title}) [PID: {pid}]"
                        : $"{procName} [PID: {pid}]";

                    results[pid] = new ActiveAudioProcessInfo(pid, procName, title, label);
                }
                catch
                {
                    // Processo encerrado entre a enumeração e a consulta
                }
            }
        }

        return results.Values
            .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}