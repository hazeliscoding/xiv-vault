using System.Diagnostics;

namespace XIVault.Core.Platform;

public interface IProcessInspector
{
    /// <summary>Returns the names from <paramref name="processNames"/> that have a running process.</summary>
    IReadOnlyList<string> FindRunning(IEnumerable<string> processNames);
}

public sealed class SystemProcessInspector : IProcessInspector
{
    public IReadOnlyList<string> FindRunning(IEnumerable<string> processNames)
    {
        var running = new List<string>();
        foreach (var name in processNames)
        {
            var processes = Process.GetProcessesByName(name);
            if (processes.Length > 0)
            {
                running.Add(name);
            }

            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return running;
    }
}
