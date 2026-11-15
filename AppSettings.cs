using System.Text.Json;

namespace ScreenTranslator;

internal sealed record AppSettings(bool SupportedAppsOnly = false, bool DefaultParagraph = false,
    bool Paused = false, bool UseDeepSeek = false);

internal sealed class AppSettingsStore(string path)
{
    public static AppSettingsStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenTranslator", "settings.json"));

    public AppSettings Load()
    {
        try
        {
            using var file = File.OpenRead(path);
            return JsonSerializer.Deserialize<AppSettings>(file) ?? new AppSettings();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public bool Save(AppSettings settings)
    {
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = File.Create(temporary))
                JsonSerializer.Serialize(file, settings);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
