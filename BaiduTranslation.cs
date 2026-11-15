using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenTranslator;

internal sealed class BaiduCredentials(string appId, string secret)
{
    public string AppId { get; } = appId;

    // The API requires a lowercase MD5 signature of the original, unencoded text.
    public string Sign(string text, string salt) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(AppId + text + salt + secret))).ToLowerInvariant();
}

internal sealed partial class Translator
{
    private static readonly Uri BaiduEndpoint = new("https://fanyi-api.baidu.com/api/trans/vip/translate");
    private readonly SemaphoreSlim baiduRequests = new(1, 1);
    private long nextBaiduRequest;

    private async Task<string> BaiduAsync(string text, BaiduCredentials credentials, CancellationToken token)
    {
        var translated = new List<string>();
        foreach (var segment in SplitTranslationText(text, 1000))
        {
            await baiduRequests.WaitAsync(token);
            try
            {
                // The standard plan allows one request per second.
                var delay = nextBaiduRequest - Environment.TickCount64;
                if (delay > 0)
                    await Task.Delay((int)delay, token);
                nextBaiduRequest = Environment.TickCount64 + 1000;

                var salt = RandomNumberGenerator.GetInt32(100_000_000, int.MaxValue).ToString(CultureInfo.InvariantCulture);
                var form = new Dictionary<string, string>
                {
                    ["q"] = segment, ["from"] = "en", ["to"] = "zh", ["appid"] = credentials.AppId,
                    ["salt"] = salt, ["sign"] = credentials.Sign(segment, salt)
                };
                translated.Add(await PreferDirectAsync(BaiduEndpoint, TimeSpan.FromSeconds(6), async (client, cancellation) =>
                {
                    using var content = new FormUrlEncodedContent(form);
                    using var response = await client.PostAsync(BaiduEndpoint, content, cancellation);
                    response.EnsureSuccessStatusCode();
                    using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation),
                        cancellationToken: cancellation);
                    return ParseBaidu(json.RootElement);
                }, token, isPaidRequest: true));
            }
            finally
            {
                baiduRequests.Release();
            }
        }
        return string.Join(' ', translated);
    }

    internal static string ParseBaidu(JsonElement root)
    {
        if (root.TryGetProperty("error_code", out var error))
        {
            var code = error.ToString();
            if (code is "52003" or "54001")
                throw new TranslationAuthenticationException();
            if (code != "0")
                throw new InvalidDataException("Baidu translation request was unsuccessful.");
        }

        var lines = root.GetProperty("trans_result").EnumerateArray()
            .Select(line => line.GetProperty("dst").GetString()?.Trim()).ToArray();
        if (lines.Length == 0 || lines.Any(string.IsNullOrEmpty))
            throw new NoTranslationException();
        return string.Join('\n', lines);
    }
}
