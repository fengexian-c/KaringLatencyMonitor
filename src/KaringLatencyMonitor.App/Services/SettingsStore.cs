using System.Text.Json;
using KaringLatencyMonitor.App.Models;
using Windows.Security.Credentials;

namespace KaringLatencyMonitor.App.Services;

public sealed class SettingsStore
{
    private const string CredentialResource = "KaringLatencyMonitor.Controller";
    private const string CredentialUser = "default";
    public async Task<(AppSettings Settings, string Secret)> LoadAsync()
    {
        AppSettings settings;
        try
        {
            if (!File.Exists(AppPaths.SettingsPath))
            {
                settings = AppSettings.Default;
            }
            else
            {
                await using var stream = File.OpenRead(AppPaths.SettingsPath);
                settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                    stream,
                    AppJsonSerializerContext.Default.AppSettings).ConfigureAwait(false)
                    ?? AppSettings.Default;
            }
        }
        catch (JsonException)
        {
            settings = AppSettings.Default;
        }

        return (settings, LoadSecret());
    }

    public async Task SaveAsync(AppSettings settings, string secret)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var temporaryPath = AppPaths.SettingsPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    AppJsonSerializerContext.Default.AppSettings)
                .ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        File.Move(temporaryPath, AppPaths.SettingsPath, true);
        SaveSecret(secret);
    }

    private static string LoadSecret()
    {
        try
        {
            var vault = new PasswordVault();
            var credential = vault.Retrieve(CredentialResource, CredentialUser);
            credential.RetrievePassword();
            return credential.Password ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void SaveSecret(string secret)
    {
        var vault = new PasswordVault();
        try
        {
            var existing = vault.Retrieve(CredentialResource, CredentialUser);
            vault.Remove(existing);
        }
        catch
        {
            // The credential does not exist yet.
        }

        if (!string.IsNullOrEmpty(secret))
        {
            vault.Add(new PasswordCredential(CredentialResource, CredentialUser, secret));
        }
    }
}
