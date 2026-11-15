using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenTranslator;

internal sealed class TranslationCredentials
{
    public string? BaiduAppId { get; init; }
    public string? BaiduSecretKey { get; init; }
    public string? DeepSeekApiKey { get; init; }

    public BaiduCredentials? GetBaidu() => BaiduAppId is { Length: > 0 } appId && BaiduSecretKey is { Length: > 0 } secret
        ? new BaiduCredentials(appId, secret)
        : null;
}

internal sealed class TranslationCredentialStore(string path)
{
    public static TranslationCredentialStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenTranslator", "credentials.dat"));

    public TranslationCredentials Load()
    {
        try
        {
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<TranslationCredentials>(clear) ?? new TranslationCredentials();
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return new TranslationCredentials();
        }
    }

    public bool Save(TranslationCredentials credentials)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(credentials);
        var temporary = path + ".tmp";
        try
        {
            var encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}
