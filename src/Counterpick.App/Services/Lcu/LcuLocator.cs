using System.Diagnostics;
using System.IO;
using System.Management;

namespace Counterpick.App.Services.Lcu;

/// <summary>
/// Finds the running League client. Returns null when it is not running, which is a
/// normal state - the watcher polls this rather than treating it as an error.
/// </summary>
public static class LcuLocator
{
    private const string ProcessName = "LeagueClientUx";

    public static LcuEndpoint? Find()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(ProcessName);
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            if (processes.Length == 0) return null;

            // Preferred: the port and token straight off the command line.
            var (commandLine, exePath) = QueryProcess();
            if (LcuEndpoint.ParseCommandLine(commandLine) is { } fromArgs) return fromArgs;

            // Fallback 1: the install directory named on the command line, then its lockfile.
            if (LcuEndpoint.ReadLockfile(LcuEndpoint.InstallDirectoryFrom(commandLine)) is { } fromArgDir)
                return fromArgDir;

            // Fallback 2: the folder the exe lives in, from WMI or from the process itself.
            var exeDir = exePath is not null ? Path.GetDirectoryName(exePath) : MainModuleDirectory(processes[0]);
            return LcuEndpoint.ReadLockfile(exeDir);
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    /// <summary>
    /// Process.StartInfo.Arguments is empty for processes we did not start, so the command
    /// line has to come from WMI.
    /// </summary>
    private static (string? CommandLine, string? ExecutablePath) QueryProcess()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine, ExecutablePath FROM Win32_Process WHERE Name = '{ProcessName}.exe'");
            using var results = searcher.Get();
            foreach (var o in results)
            {
                using (o)
                {
                    var cmd = o["CommandLine"] as string;
                    var exe = o["ExecutablePath"] as string;
                    if (!string.IsNullOrEmpty(cmd) || !string.IsNullOrEmpty(exe)) return (cmd, exe);
                }
            }
        }
        catch (Exception)
        {
            // WMI unavailable or access denied. The lockfile fallback still works.
        }
        return (null, null);
    }

    private static string? MainModuleDirectory(Process p)
    {
        try
        {
            return Path.GetDirectoryName(p.MainModule?.FileName);
        }
        catch (Exception)
        {
            // Access denied when the client runs elevated and we do not.
            return null;
        }
    }
}
