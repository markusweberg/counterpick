using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Counterpick.App.Services;

/// <summary>
/// User settings, stored as JSON in %APPDATA%\Counterpick\config.json.
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// Anthropic API key, in memory only. Everyone who runs Counterpick brings their own key
    /// and pays for their own calls; no build ever carries one. On disk it is written as
    /// <see cref="ApiKeyProtected"/>, never in the clear.
    /// </summary>
    [JsonIgnore]
    public string? ApiKey { get; set; }

    /// <summary>
    /// The key encrypted with Windows DPAPI for the current user: only this Windows account
    /// on this machine can read it back. A config.json that leaks through a backup, a synced
    /// folder or a copy to another PC carries a blob, not a key.
    /// </summary>
    public string? ApiKeyProtected
    {
        get => Protect(ApiKey);
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            var key = Unprotect(value);
            if (key is null) KeyUnreadable = true;
            else ApiKey = key;
        }
    }

    /// <summary>
    /// A stored key that could not be decrypted - the config came from another machine or
    /// another Windows account. The key is gone for good; Settings asks for it again.
    /// </summary>
    [JsonIgnore]
    public bool KeyUnreadable { get; private set; }

    /// <summary>
    /// The plain-text key an older build wrote as "ApiKey". Read once, then the file is
    /// rewritten encrypted by <see cref="Load"/>. Never written.
    /// </summary>
    [JsonPropertyName("ApiKey")]
    public string? LegacyApiKey
    {
        get => null;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            ApiKey ??= value;
            _migrated = true;
        }
    }

    private bool _migrated;

    /// <summary>
    /// Only needed for an organisation-level key, which the API refuses without an
    /// <c>anthropic-workspace-id</c> header. A key created inside a workspace needs none.
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>
    /// Model for the full matchup brief. Read on the loading screen, so it can afford the
    /// stronger model's extra seconds. A family ("opus") runs on its newest model; a full
    /// id pins one. See <see cref="ClaudeClient.Families"/>.
    /// </summary>
    public string Model { get; set; } = "opus";

    /// <summary>
    /// Model for the ranked shortlist during pick phase, where the whole answer has to
    /// land inside a 27-second timer. Sonnet is the latency choice.
    /// </summary>
    public string ShortlistModel { get; set; } = "sonnet";

    /// <summary>
    /// Whether the pool is ranked during pick phase at all. Off saves every shortlist call
    /// for a player who already knows what they will pick; the brief is still written.
    /// </summary>
    public bool ShortlistEnabled { get; set; } = true;

    /// <summary>The role you queue for. Drives which enemy pick counts as your lane opponent.</summary>
    public string PrimaryRole { get; set; } = "Top";

    /// <summary>Data Dragon patch the cached art belongs to. Empty means "fetch latest".</summary>
    public string? DataDragonVersion { get; set; }

    /// <summary>
    /// Folder (or URL) the installed app checks for newer versions. Empty means the one
    /// baked in by tools/release.ps1; set it to move releases without repacking.
    /// </summary>
    public string? UpdateSource { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Ties the blob to this app: another program running as the same user cannot decrypt
    // it by calling DPAPI with no entropy. Not a secret, only a namespace.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Counterpick.AnthropicApiKey.v1");

    private static string? Protect(string? key) =>
        string.IsNullOrEmpty(key)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string blob)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

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
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile), Json)
                         ?? new AppConfig();
            // Older builds stored exact ids from a fixed list. Those become their family, so
            // they follow new releases like a fresh install; a hand-typed id stays pinned.
            config.Model = config.ToFamily(config.Model);
            config.ShortlistModel = config.ToFamily(config.ShortlistModel);
            // An older build left the key in the clear; the first load takes it off disk.
            if (config._migrated) config.Save();
            return config;
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

    private string ToFamily(string model)
    {
        var family = model switch
        {
            "claude-opus-5" => "opus",
            "claude-sonnet-5" => "sonnet",
            "claude-haiku-4-5" => "haiku",
            _ => null
        };
        if (family is null) return model;
        _migrated = true;
        return family;
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(this, Json));
        KeyUnreadable = KeyUnreadable && ApiKey is null;
    }
}
