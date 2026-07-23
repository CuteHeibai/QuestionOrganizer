using System.Text.Json;
using EaxmBuilder.Core;

namespace EaxmBuilder.Infrastructure;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public SettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuestionOrganizer");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(_settingsPath)) return new AppSettings();

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions)
                           ?? new AppSettings();
            MigrateProviderProfile(settings);
            MigrateOutputDirectories(settings);
            return settings;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
    }

    public string ReadApiKey(AppSettings settings) => ReadApiKey(settings, settings.Provider);

    public string ReadApiKey(AppSettings settings, AiProviderKind provider)
    {
        try
        {
            var profile = settings.ProviderProfiles.TryGetValue(provider, out var savedProfile)
                ? savedProfile
                : null;
            var protectedApiKey = profile?.ProtectedApiKey;
            if (string.IsNullOrWhiteSpace(protectedApiKey) && settings.Provider == provider)
                protectedApiKey = settings.ProtectedApiKey;
            return WindowsDataProtector.Unprotect(protectedApiKey ?? string.Empty);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void MigrateProviderProfile(AppSettings settings)
    {
        if (settings.ProviderProfiles.ContainsKey(settings.Provider)) return;
        if (string.IsNullOrWhiteSpace(settings.ProtectedApiKey) &&
            string.IsNullOrWhiteSpace(settings.BaseUrl) &&
            string.IsNullOrWhiteSpace(settings.Model))
            return;

        settings.ProviderProfiles[settings.Provider] = new AiProviderSettings
        {
            ProtectedApiKey = settings.ProtectedApiKey,
            BaseUrl = settings.BaseUrl,
            Model = settings.Model
        };
    }

    private static void MigrateOutputDirectories(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.OutputDirectory))
            settings.OutputDirectory = new AppSettings().OutputDirectory;
        if (string.IsNullOrWhiteSpace(settings.FinalOutputDirectory))
            settings.FinalOutputDirectory = Path.Combine(settings.OutputDirectory, "最终输出");
    }
}
