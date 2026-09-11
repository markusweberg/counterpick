using System.IO;
using System.Text.RegularExpressions;

namespace Counterpick.App.Services.Lcu;

/// <summary>
/// Where the League client is listening right now. Both values change on every client
/// restart, so this is re-resolved on each reconnect and never persisted.
/// </summary>
public sealed partial record LcuEndpoint(int Port, string Password)
{
    public string BaseUrl => $"https://127.0.0.1:{Port}";

    /// <summary>
    /// LeagueClientUx.exe is launched with <c>--app-port=NNNN</c> and
    /// <c>--remoting-auth-token=XXXX</c> on its command line. Reading those is the most
    /// reliable route because it needs no guess at the install directory.
    /// </summary>
    public static LcuEndpoint? ParseCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var port = AppPort().Match(commandLine);
        var token = AuthToken().Match(commandLine);
        if (!port.Success || !token.Success) return null;
        return int.TryParse(port.Groups[1].Value, out var p)
            ? new LcuEndpoint(p, token.Groups[1].Value)
            : null;
    }

    /// <summary>The install directory named on the same command line, for the lockfile fallback.</summary>
    public static string? InstallDirectoryFrom(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var m = InstallDir().Match(commandLine);
        if (!m.Success) return null;
        var dir = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return dir.TrimEnd('\\', '/');
    }

    /// <summary>
    /// The documented fallback: <c>lockfile</c> in the install directory holds
    /// <c>name:pid:port:password:protocol</c> on a single line.
    /// </summary>
    public static LcuEndpoint? ParseLockfile(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var parts = content.Trim().Split(':');
        if (parts.Length < 5) return null;
        return int.TryParse(parts[2], out var port) && parts[3].Length > 0
            ? new LcuEndpoint(port, parts[3])
            : null;
    }

    /// <summary>Read the lockfile in a directory, tolerating the client holding it open.</summary>
    public static LcuEndpoint? ReadLockfile(string? installDirectory)
    {
        if (installDirectory is null) return null;
        var path = Path.Combine(installDirectory, "lockfile");
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return ParseLockfile(reader.ReadToEnd());
        }
        catch (IOException)
        {
            return null;
        }
    }

    [GeneratedRegex("--app-port=\"?(\\d+)")]
    private static partial Regex AppPort();

    [GeneratedRegex("--remoting-auth-token=\"?([A-Za-z0-9_\\-]+)")]
    private static partial Regex AuthToken();

    // The client quotes each whole argument: "--install-directory=C:\Riot Games\League of Legends".
    // So the value runs to the closing quote, or to the next " --" if it is unquoted.
    [GeneratedRegex("--install-directory=(?:\"([^\"]+)\"|([^\"]+?))(?:\"|\\s+--|$)")]
    private static partial Regex InstallDir();
}
