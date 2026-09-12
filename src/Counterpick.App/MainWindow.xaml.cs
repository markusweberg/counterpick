using System.IO;
using System.Net.Sockets;
using System.Windows;
using Counterpick.App.Services;
using Counterpick.App.Services.Lcu;
using Microsoft.Web.WebView2.Core;

namespace Counterpick.App;

public partial class MainWindow : Window
{
    /// <summary>
    /// The packaged UI is served from this made-up host rather than file://, so the page
    /// gets a real secure origin. Without it, ES modules and fetch behave differently
    /// from how they do in a browser and the app is a pain to debug.
    /// </summary>
    private const string VirtualHost = "app.counterpick";

    private const int ViteDevPort = 5173;

    private readonly AppConfig _config;
    private readonly Storage _storage;
    private readonly BriefCache _briefs;
    private readonly BackupService _backups;
    private readonly ChampionCatalog _catalog = new();
    private readonly RoleRates _rates = new();
    private readonly ClaudeClient _claude;
    private LcuWatcher? _watcher;
    private Bridge? _bridge;

    /// <summary>
    /// Whether this WebView2 runtime can hand the page's drag region to the window. Until
    /// it can, the caption strip stays zero-height so nothing eats the page's clicks.
    /// </summary>
    private bool _canDragFromPage;

    public MainWindow()
    {
        InitializeComponent();

        AppPaths.EnsureCreated();
        Trace.Start();
        _config = AppConfig.Load();

        _storage = new Storage();
        _backups = new BackupService(_storage);

        // Snapshot before any schema upgrade touches your notes - the riskiest moment
        // in the app's life, and the cheapest one to insure.
        _storage.EnsureSchema(beforeMigration: _ => _backups.Snapshot("migration"));

        _briefs = new BriefCache();
        _briefs.EnsureSchema();

        _claude = new ClaudeClient(_config);

        // A fresh install with an empty database pulls the OneDrive mirror back in.
        // Nothing local to lose, so no need to ask - and nothing to lose either if the
        // mirror is unreadable, so it must never stop the app from opening.
        try
        {
            _backups.RestoreFromMirrorIfEmpty();
        }
        catch (Exception)
        {
            // The Data panel still offers a manual restore, which reports its error.
        }

        // One snapshot per session, and only when something actually changed.
        _backups.SnapshotIfChanged("startup");

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// The native window handle exists from here on; that is what the title-bar paint needs.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TitleBar.Paint(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Keep the WebView2 profile in %APPDATA% rather than beside the exe, so a clean
        // rebuild does not wipe it and the output folder stays disposable.
        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: AppPaths.WebViewDir);

        await Web.EnsureCoreWebView2Async(env);
        var core = Web.CoreWebView2;

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.AreDevToolsEnabled = true;   // F12 stays available; this is a personal tool

        // The window has no caption of its own (see MainWindow.xaml), so the page draws
        // one and marks it `app-region: drag`. Two halves have to meet for that to
        // actually move the window:
        //
        //   1. WebView2 must report those regions as non-client, so a click on them
        //      falls through the web view's own HWND to the window underneath. That is
        //      this switch.
        //   2. WindowChrome underneath must then agree that the strip is a caption.
        //      It answers the hit test from CaptionHeight alone and knows nothing about
        //      the page, so a CaptionHeight of zero swallows the fall-through and the
        //      window stays put however well the page is marked up.
        //
        // So the height is left at zero until the first half is confirmed, and the page
        // then reports what its topbar actually measures - it wraps to two rows on a
        // narrow window, and a guess here would put the drag strip in the wrong place.
        // A runtime too old for the property keeps a page that cannot be dragged, which
        // is survivable; a page that cannot be clicked is not.
        try
        {
            core.Settings.IsNonClientRegionSupportEnabled = true;
            _canDragFromPage = true;
        }
        catch (NotImplementedException)
        {
            Trace.Write("window", "WebView2 runtime has no non-client region support; the title bar will not drag.");
        }

        // The watcher emits through the bridge, so the bridge has to exist first. Its
        // constructor takes the watcher, so the watcher gets a forwarding lambda.
        _watcher = new LcuWatcher(_catalog, (name, payload) => _bridge?.Emit(name, payload));
        var updates = new UpdateService(_config, (name, payload) => _bridge?.Emit(name, payload));
        _bridge = new Bridge(Web, _storage, _config, _backups, _briefs, _catalog, _watcher, _claude, _rates, updates);
        _watcher.Start();

        // Fetch a newer version in the background so it is a one-click restart by the
        // time you look. Only meaningful for an installed build; a bin/ build reports
        // that and stops. A failure here is a line in Settings, never a dialog.
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => updates.CheckAsync());

        // Warm the play-rate table so the first enemy pick is placed without waiting on
        // the network. Any failure falls back to the bundled snapshot inside the loader.
        _ = _rates.EnsureLoadedAsync().ContinueWith(
            _ => Trace.Write("roles", $"play rates ready: {_rates.Source}, patch {_rates.Patch ?? "?"}, {_rates.Count} champions"));

        // Open real links in the user's browser instead of hijacking the app window.
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args.Uri)
                {
                    UseShellExecute = true
                });
        };

        Web.Source = ResolveUiSource(core);
    }

    /// <summary>
    /// Tell the window how tall the page's own title bar is, in CSS pixels - which are
    /// WPF device-independent pixels, so the number crosses unchanged at any DPI. The
    /// page calls this after every render; see `window.setCaptionHeight` in Bridge.cs.
    ///
    /// Ignored when the runtime cannot hand drag regions to the window, because then a
    /// caption strip would only take clicks away from the page without giving anything
    /// back.
    /// </summary>
    public void SetCaptionHeight(double height)
    {
        if (!_canDragFromPage) return;
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome is null) return;

        // Clamped: a broken measurement must not turn a third of the window into caption.
        var wanted = Math.Clamp(height, 0, 160);
        if (Math.Abs(chrome.CaptionHeight - wanted) < 0.5) return;
        chrome.CaptionHeight = wanted;
    }

    /// <summary>
    /// Capture whatever was written this session before the process goes away.
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        _watcher?.Dispose();
        try
        {
            _backups.SnapshotIfChanged("shutdown");

            // Leave the database in one piece for OneDrive to carry: with the -wal folded
            // in, the file it uploads after we exit is a complete database rather than
            // half of a pair.
            _storage.Checkpoint();
        }
        catch (Exception)
        {
            // Never block shutdown over a backup; the next startup takes one.
        }
    }

    /// <summary>
    /// During development, prefer the Vite dev server if it happens to be running - that
    /// gives hot reload while styling, without rebuilding the C# app. Otherwise serve the
    /// copy of dist/ that MSBuild put next to the exe.
    /// </summary>
    private static Uri ResolveUiSource(CoreWebView2 core)
    {
#if DEBUG
        if (IsListening(ViteDevPort))
            return new Uri($"http://localhost:{ViteDevPort}/");
#endif
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!File.Exists(Path.Combine(wwwroot, "index.html")))
            return new Uri("data:text/html," + Uri.EscapeDataString(MissingFrontendPage(wwwroot)));

        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        return new Uri($"https://{VirtualHost}/index.html");
    }

    private static bool IsListening(int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", port).Wait(150) && c.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string MissingFrontendPage(string wwwroot) => $$"""
        <body style="background:#010A13;color:#F0E6D2;font:14px system-ui;padding:40px;line-height:1.6">
          <h2 style="color:#C8AA6E">Frontend not built</h2>
          <p>No <code>index.html</code> under <code>{{wwwroot}}</code>.</p>
          <p>Run <code>dotnet build</code> from the repository root, or start the dev server
             with <code>npm run dev</code> in <code>src/Counterpick.Web</code>.</p>
        </body>
        """;
}
