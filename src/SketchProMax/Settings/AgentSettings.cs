using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SketchProMax.Settings;

/// <summary>
/// Per-user settings stored in %APPDATA%\SketchProMax\settings.json.
/// The API key is encrypted with Windows DPAPI (current user) and never written in plain text.
/// The ANTHROPIC_API_KEY environment variable, when set, takes precedence.
/// </summary>
public sealed class AgentSettings
{
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>low | medium | high | xhigh | max</summary>
    public string Effort { get; set; } = "high";

    public int MaxTokens { get; set; } = 16000;

    /// <summary>Upper bound on Claude round-trips for one user message.</summary>
    public int MaxToolRounds { get; set; } = 25;

    /// <summary>Ask before running tools that change the model.</summary>
    public bool RequireApprovalForChanges { get; set; } = true;

    /// <summary>Firm standards appended to the system prompt (naming, sheet numbering, tag styles...).</summary>
    public string FirmStandards { get; set; } = "";

    public string? EncryptedApiKey { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "settings.json");

    public static AgentSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string? GetApiKey()
    {
        var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        if (EncryptedApiKey is null || !OperatingSystem.IsWindows()) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(EncryptedApiKey), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException) { return null; }
    }

    public void SetApiKey(string key)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
        EncryptedApiKey = Convert.ToBase64String(bytes);
    }
}
