using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Desktop;

/// <summary>Where the app keeps its own files and its own node's (phase 115).</summary>
public static class AppPaths
{
    /// <summary><c>%LOCALAPPDATA%\InferHub</c>, <c>~/.local/share/InferHub</c>, <c>~/Library/Application Support/InferHub</c>.</summary>
    public static string Root =>
        Environment.GetEnvironmentVariable("INFERHUB_DESKTOP_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "InferHub");

    public static string Desktop => Path.Combine(Root, "Desktop");

    /// <summary>The app's own node: its data, its token, its <c>node.local.json</c>.</summary>
    public static string NodeData => Path.Combine(Root, "Node");

    public static string SettingsFile => Path.Combine(Desktop, "settings.json");
}

/// <summary>The app's own settings — which node it controls and how (115 D2). Never the node's settings.</summary>
public sealed class DesktopSettings
{
    /// <summary><c>auto</c> (attach if a node answers, else offer to run one), <c>attach</c> or <c>embedded</c>.</summary>
    public string Mode { get; set; } = "auto";

    /// <summary>The node's local API, e.g. <c>http://localhost:5081</c>. Null: the default.</summary>
    public string? Url { get; set; }

    /// <summary>A control token pasted by the user (the Windows service's is readable by administrators only).</summary>
    public string? Token { get; set; }

    /// <summary>A key for the node's local API (<c>LocalApi:ApiKeys</c>), for the Chat tab.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Start the app's own node when the app starts.</summary>
    public bool StartEmbeddedOnLaunch { get; set; }

    /// <summary>Closing the window keeps the app in the tray.</summary>
    public bool CloseToTray { get; set; } = true;

    [JsonIgnore]
    public string? LoadError { get; private set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static DesktopSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;

        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path), Json) ?? new DesktopSettings()
                : new DesktopSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new DesktopSettings { LoadError = ex.Message };
        }
    }

    /// <summary>Saved readable by its owner only where the platform says so — it may hold a control token.</summary>
    public void Save(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path + ".tmp", options);
        JsonSerializer.Serialize(stream, this, Json);
        stream.Close();
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
