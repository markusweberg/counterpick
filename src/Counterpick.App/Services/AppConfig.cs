using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Counterpick.App.Services;

/// <summary>
/// User settings, stored as JSON in %APPDATA%\Counterpick\config.json.
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// Anthropic API key. Local-file storage is deliberate and adequate while this is a
    /// single-user app on your own machine. If Counterpick is ever shared, the key has to
    /// move behind a backend instead - a shipped build cannot hold a key safely.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Model used for briefs. Sonnet is the latency/quality balance for champ select.</summary>
    public string Model { get; set; } = "claude-sonnet-5";

    /// <summary>The role you queue for. Drives which enemy pick counts as your lane opponent.</summary>
    public string PrimaryRole { get; set; } = "Top";

    /// <summary>Data Dragon patch the cached art belongs to. Empty means "fetch latest".</summary>
    public string? DataDragonVersion { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppConfig Load()
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(AppPaths.ConfigFile))
        {
            var fresh = new AppConfig();
            fresh.Save();
            return fresh;
        }

        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile), Json)
                   ?? new AppConfig();
        }
        catch (JsonException)
        {
            // A hand-edited config that no longer parses should not stop the app from
            // starting. Keep the broken file so the user can see what happened.
            var broken = AppPaths.ConfigFile + ".broken";
            File.Copy(AppPaths.ConfigFile, broken, overwrite: true);
            return new AppConfig();
        }
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(this, Json));
    }
}
