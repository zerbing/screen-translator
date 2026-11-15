using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenTranslator;

internal sealed partial class OfflineDictionary : IDisposable
{
    public const string Source = "ECDICT · 离线";

    private readonly object archiveLock = new();
    private readonly Lazy<ZipArchive> archive = new(() => new ZipArchive(
        typeof(OfflineDictionary).Assembly.GetManifestResourceStream("ScreenTranslator.Dictionaries.ECDICT.zip")
            ?? throw new InvalidDataException("The ECDICT dictionary is missing.")));

    [GeneratedRegex(@"^[A-Za-z\u00C0-\u00D6\u00D8-\u00F6\u00F8-\u024F]+(?:['-][A-Za-z\u00C0-\u00D6\u00D8-\u00F6\u00F8-\u024F]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Headword();

    public string? Find(string text)
    {
        var word = TermGlossary.Normalize(text.Trim()).Normalize(NormalizationForm.FormC);
        if (word.Length > 64 || !Headword().IsMatch(word))
            return null;

        var exact = ReadWord(word);
        if (exact is not null || word.All(char.IsAscii))
            return exact;
        var plain = string.Concat(word.Normalize(NormalizationForm.FormD)
            .Where(letter => CharUnicodeInfo.GetUnicodeCategory(letter) != UnicodeCategory.NonSpacingMark));
        return plain == word ? null : ReadWord(plain);
    }

    private string? ReadWord(string word)
    {
        var folded = word.ToLowerInvariant();
        var prefix = folded[..Math.Min(2, folded.Length)];
        lock (archiveLock)
        {
            var entry = archive.Value.GetEntry(prefix);
            if (entry is null)
                return null;

            using var reader = new StreamReader(entry.Open());
            string? fallback = null;
            while (reader.ReadLine() is { } line)
            {
                var delimiter = line.IndexOf('\t');
                var key = line.AsSpan(0, delimiter);
                if (!key.Equals(word, StringComparison.OrdinalIgnoreCase))
                    continue;

                var definition = line[(delimiter + 1)..].Replace('\t', '\n');
                if (key.SequenceEqual(word))
                    return definition;
                if (fallback is null || key.SequenceEqual(folded))
                    fallback = definition;
            }
            return fallback;
        }
    }

    public void Dispose()
    {
        lock (archiveLock)
        {
            if (archive.IsValueCreated)
                archive.Value.Dispose();
        }
    }
}
