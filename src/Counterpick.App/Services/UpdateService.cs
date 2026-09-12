using System.Diagnostics;
using System.IO;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Counterpick.App.Services;

/// <summary>
/// Keeps the installed app current.
///
/// Velopack installs the app under %LOCALAPPDATA%\Counterpick and puts the shortcuts in
/// place; this class is the half that runs afterwards. It watches a release folder -
/// the one <c>tools/release.ps1</c> packs into - and when a newer version is there it
/// downloads it in the background and offers a restart. The folder is baked into the
/// build by the release script, so whoever packs a release decides where updates come
/// from; <see cref="AppConfig.UpdateSource"/> overrides it per machine.
///
/// A build run from bin/ or from Visual Studio is not installed and cannot be updated in
/// place. It still reports its version, and says why the buttons are inert.
///
/// Your data is untouched by all of this: it lives in %APPDATA%, the install lives in
/// %LOCALAPPDATA%, and an update only swaps the second.
/// </summary>
public sealed class UpdateService
{
    private readonly AppConfig _config;
    private readonly Action<string, object?> _emit;
    private readonly object _gate = new();

    private UpdateManager? _manager;
    private UpdateInfo? _pending;
    private string _state = "idle";
    private string? _available;
    private int _progress;
    private string? _error;
    private DateTimeOffset? _checkedAt;
    private bool _busy;

    public UpdateService(AppConfig config, Action<string, object?> emit)
    {
        _config = config;
        _emit = emit;
    }

    /// <summary>"1.2.3" from the assembly, without the +commit suffix the SDK appends.</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>
    /// Where releases are read from. The release script bakes its output folder into the
    /// assembly; config.json can point somewhere else. Null when nothing was baked in.
    /// </summary>
    public string? Source =>
        string.IsNullOrWhiteSpace(_config.UpdateSource) ? BakedSource : _config.UpdateSource;

    private static string? BakedSource { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateSource")?.Value is { Length: > 0 } s ? s : null;

    /// <summary>True when running from a Velopack install rather than a build folder.</summary>
    public bool Installed => Manager()?.IsInstalled ?? false;

    /// <summary>Everything the Settings panel shows. Also the payload of every event.</summary>
    public object View()
    {
        lock (_gate)
        {
            return new
            {
                version = Version,
                installed = Installed,
                source = Source,
                state = _state,
                available = _available,
                progress = _progress,
                error = _error,
                checkedAt = _checkedAt?.ToString("o")
            };
        }
    }

    /// <summary>
    /// Look for a newer version and, when there is one, download it so a restart is all
    /// that is left. Safe to call repeatedly; a check already in flight is left alone.
    /// </summary>
    public async Task<object?> CheckAsync()
    {
        lock (_gate)
        {
            if (_busy) return View();
            _busy = true;
        }

        try
        {
            var mgr = Manager();
            if (mgr is null)
            {
                Set("error", error: Source is null
                    ? "This build has no update source. Pack it with tools/release.ps1."
                    : $"Release folder not found: {Source}");
                return View();
            }
            if (!mgr.IsInstalled)
            {
                Set("not-installed");
                return View();
            }

            Set("checking", error: null);
            var info = await mgr.CheckForUpdatesAsync();
            if (info is null)
            {
                Trace.Write("update", $"{Version} is current; nothing newer in {Source}");
                Set("up-to-date", available: null, checkedAt: DateTimeOffset.Now);
                return View();
            }

            var target = info.TargetFullRelease.Version.ToString();
            Trace.Write("update", $"{target} available in {Source}, downloading");
            Set("downloading", available: target, progress: 0, checkedAt: DateTimeOffset.Now);

            await mgr.DownloadUpdatesAsync(info, pct => Set("downloading", progress: pct));

            lock (_gate) _pending = info;
            Set("ready", progress: 100);
            Trace.Write("update", $"{target} downloaded, waiting for restart");
            return View();
        }
        catch (Exception ex)
        {
            Trace.Write("update", $"check failed: {ex.GetType().Name}: {ex.Message}");
            Set("error", error: ex.Message, checkedAt: DateTimeOffset.Now);
            return View();
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    /// <summary>Hand over to Update.exe: it swaps the install and relaunches the app.</summary>
    public void Restart()
    {
        UpdateInfo? info;
        lock (_gate) info = _pending;
        if (info is null || Manager() is not { } mgr)
            throw new InvalidOperationException("No update has been downloaded yet.");
        Trace.Write("update", $"restarting into {info.TargetFullRelease.Version}");
        mgr.ApplyUpdatesAndRestart(info);
    }

    private UpdateManager? Manager()
    {
        if (_manager is not null) return _manager;
        var source = Source;
        if (source is null) return null;

        // A missing folder is not fatal - the drive may be unplugged, or nothing has been
        // packed yet - so report it rather than throw from a constructor.
        if (!source.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(source))
            return null;

        _manager = new UpdateManager(source);
        return _manager;
    }

    private void Set(string state, string? available = "\0", int? progress = null,
                     string? error = "\0", DateTimeOffset? checkedAt = null)
    {
        lock (_gate)
        {
            _state = state;
            if (available != "\0") _available = available;
            if (progress is { } p) _progress = p;
            if (error != "\0") _error = error;
            if (checkedAt is { } t) _checkedAt = t;
        }
        _emit("update.state", View());
    }

    private static string ResolveVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
