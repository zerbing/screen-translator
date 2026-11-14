using System.Text.Json;

namespace ScreenTranslator;

internal sealed class TranslationCache(string path)
{
    private const int Capacity = 512;
    private const int MaxFileLength = 8 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, TranslationResult> results = new(StringComparer.Ordinal);
    private readonly Queue<string> insertionOrder = new();
    private bool loaded;

    public async Task<TranslationResult?> GetAsync(string key, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!loaded)
                await LoadAsync(token).ConfigureAwait(false);
            return results.GetValueOrDefault(key);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task LoadAsync(CancellationToken token)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            if (file.Length <= MaxFileLength)
            {
                var entries = await JsonSerializer.DeserializeAsync<CacheEntry[]>(file, cancellationToken: token).ConfigureAwait(false);
                foreach (var entry in entries ?? [])
                {
                    if (entry?.Key is { Length: 64 } && !string.IsNullOrWhiteSpace(entry.Result?.Text))
                        Remember(entry.Key, entry.Result);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or unusable storage falls back to the in-memory cache.
        }
        loaded = true;
    }

    public async Task StoreAsync(string key, TranslationResult result)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Remember(key, result);
            await WriteAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private void Remember(string key, TranslationResult result)
    {
        if (!results.ContainsKey(key))
        {
            if (results.Count == Capacity)
                results.Remove(insertionOrder.Dequeue());
            insertionOrder.Enqueue(key);
        }
        results[key] = result;
    }

    private async Task WriteAsync()
    {
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                var snapshot = insertionOrder.Select(key => new CacheEntry(key, results[key])).ToArray();
                await JsonSerializer.SerializeAsync(file, snapshot).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Keep serving translations when the cache directory is unwritable.
        }
    }

    private sealed record CacheEntry(string Key, TranslationResult Result);
}
