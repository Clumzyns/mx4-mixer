using System.Diagnostics;

namespace Mixer.Services;

public static class SteelSeriesApps
{
    private static readonly string[] Names =
    [
        "steelseriesgg",
        "steelseriesggclient",
        "steelseriesengine",
        "steelseriesengine3",
        "steelseriessonar",
        "sonar"
    ];

    public static IReadOnlyList<string> Running()
    {
        var found = new List<string>();
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return found; }

        foreach (var process in processes)
        {
            try
            {
                var name = process.ProcessName;
                if (Names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    found.Add(name);
            }
            catch { /* process exited */ }
            finally { process.Dispose(); }
        }

        return found;
    }

    public static int Close()
    {
        var closed = 0;
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return 0; }

        foreach (var process in processes)
        {
            try
            {
                if (!Names.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                    continue;
                process.Kill(entireProcessTree: true);
                closed++;
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't close " + process.ProcessName, ex);
            }
            finally { process.Dispose(); }
        }

        return closed;
    }
}
