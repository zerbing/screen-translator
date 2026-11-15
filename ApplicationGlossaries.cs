namespace ScreenTranslator;

internal sealed class ApplicationGlossaries
{
    private sealed record Profile(string Id, Lazy<TermGlossary> Terms);
    private readonly Dictionary<string, Profile> profiles = new(StringComparer.OrdinalIgnoreCase);

    public ApplicationGlossaries()
    {
        using var reader = new StreamReader(TermGlossary.Open("Glossaries.Applications.tsv"));
        while (reader.ReadLine() is { } row)
        {
            if (row.Length == 0 || row.StartsWith('#'))
                continue;
            var fields = row.Split('\t');
            var id = fields[0];
            var profile = new Profile(id, new Lazy<TermGlossary>(() => new TermGlossary(id, fields[1])));
            foreach (var processName in fields[2].Split('|'))
                profiles.Add(processName, profile);
        }
    }

    public bool IsGenshin(string? processName) => Find(processName)?.Id == "Genshin";

    public bool IsSupported(string? processName, string? windowTitle = null) =>
        FindApplication(processName, windowTitle) is not null;

    public TermGlossary? ForProcess(string? processName, string? windowTitle = null, bool isDemoWindow = false) =>
        (isDemoWindow ? Find("YuanShen") : FindApplication(processName, windowTitle))?.Terms.Value;

    private Profile? FindApplication(string? processName, string? windowTitle)
    {
        // Java is shared by many applications. Use the game window title to recognize Minecraft.
        if (IsJava(processName) && windowTitle is not null
            && (windowTitle.Equals("Minecraft", StringComparison.OrdinalIgnoreCase)
                || windowTitle.StartsWith("Minecraft ", StringComparison.OrdinalIgnoreCase)))
            return Find("Minecraft");
        return Find(processName);
    }

    public static bool IsJava(string? processName) => string.Equals(processName, "javaw", StringComparison.OrdinalIgnoreCase)
        || string.Equals(processName, "java", StringComparison.OrdinalIgnoreCase);

    private Profile? Find(string? processName) => processName is null ? null : profiles.GetValueOrDefault(processName);
}
