using System.IO;
using System.Net.Sockets;
using System.Windows;
using Counterpick.App.Services;
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
    private Bridge? _bridge;

    public MainWindow()
    {
        InitializeComponent();

        AppPaths.EnsureCreated();
        _config = AppConfig.Load();

        _storage = new Storage();
        _backups = new BackupService(_storage);

        // Snapshot before any schema upgrade touches your notes - the riskiest moment
        // in the app's life, and the cheapest one to insure.
        _storage.EnsureSchema(beforeMigration: _ => _backups.Snapshot("migration"));

        _briefs = new BriefCache();
        _briefs.EnsureSchema();

        // A fresh install with an empty database pulls the OneDrive mirror back in.
        // Nothing local to lose, so no need to ask.
        _backups.RestoreFromMirrorIfEmpty();

        // One snapshot per session, and only when something actually changed.
        _backups.SnapshotIfChanged("startup");

        Loaded += OnLoaded;
        Closed += OnClosed;
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

        _bridge = new Bridge(Web, _storage, _config, _backups);

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
    /// Capture whatever was written this session before the process goes away.
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            _backups.SnapshotIfChanged("shutdown");
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
        <body style="background:#0B1119;color:#EDE7DA;font:14px system-ui;padding:40px;line-height:1.6">
          <h2 style="color:#D9A54C">Frontend not built</h2>
          <p>No <code>index.html</code> under <code>{{wwwroot}}</code>.</p>
          <p>Run <code>dotnet build</code> from the repository root, or start the dev server
             with <code>npm run dev</code> in <code>src/Counterpick.Web</code>.</p>
        </body>
        """;
}
