using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text.Json;
using ScreenTranslator;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var zoomMatrix = args.Contains("--zoom-matrix", StringComparer.Ordinal);
double[] requiredZooms = [1.75, 1.5, 1.25, 1.0, 0.75, 0.5];
var fixtureRoot = args.FirstOrDefault(arg => arg != "--zoom-matrix") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenTranslatorDev", "ocr-fixtures");
var failures = 0;
var checks = 0;
var settingsPath = Path.Combine(Path.GetTempPath(), $"screen-translator-settings-{Guid.NewGuid():N}.json");
try
{
    var settingsStore = new AppSettingsStore(settingsPath);
    Check("defaults enable translation in all apps", "False", settingsStore.Load().SupportedAppsOnly.ToString());
    var selectedOptions = new AppSettings(SupportedAppsOnly: true, DefaultParagraph: true, Paused: true, UseDeepSeek: true);
    Check("save selected settings", "True", settingsStore.Save(selectedOptions).ToString());
    Check("reload selected settings", selectedOptions.ToString(), new AppSettingsStore(settingsPath).Load().ToString());
    Check("preserve legacy DeepSeek selection", OnlineEngine.DeepSeek.ToString(), settingsStore.Load().SelectedEngine.ToString());
    var baiduOptions = selectedOptions with { UseDeepSeek = false, Engine = OnlineEngine.Baidu };
    Check("save Baidu selection", "True", settingsStore.Save(baiduOptions).ToString());
    Check("reload Baidu selection", baiduOptions.ToString(), new AppSettingsStore(settingsPath).Load().ToString());
    var updated = selectedOptions with { SupportedAppsOnly = false, Paused = false };
    Check("save unchecked settings", "True", settingsStore.Save(updated).ToString());
    Check("reload unchecked settings", updated.ToString(), new AppSettingsStore(settingsPath).Load().ToString());
    File.WriteAllText(settingsPath, """{"DefaultParagraph":true}""");
    Check("missing settings use defaults", new AppSettings(DefaultParagraph: true).ToString(), settingsStore.Load().ToString());
    File.WriteAllText(settingsPath, "{");
    Check("invalid settings use defaults", new AppSettings().ToString(), settingsStore.Load().ToString());
}
finally
{
    File.Delete(settingsPath);
    File.Delete(settingsPath + ".tmp");
}

var credentialsPath = Path.Combine(Path.GetTempPath(), $"screen-translator-credentials-{Guid.NewGuid():N}.dat");
try
{
    const string fixture = "not-a-real-key-for-dpapi-test";
    var store = new TranslationCredentialStore(credentialsPath);
    Check("save local credentials", "True", store.Save(new TranslationCredentials { DeepSeekApiKey = fixture }).ToString());
    Check("credentials round trip for the same Windows user", "True",
        (store.Load().DeepSeekApiKey == fixture).ToString());
    Check("credential file contains no plaintext key", "False",
        System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(credentialsPath)).Contains(fixture, StringComparison.Ordinal).ToString());
}
finally
{
    File.Delete(credentialsPath);
    File.Delete(credentialsPath + ".tmp");
}
var ocr = ScreenOcr.Create() ?? throw new InvalidOperationException("Windows English OCR is required.");
var applications = new ApplicationGlossaries();
var genshin = applications.ForProcess("YuanShen")!;
var excel = applications.ForProcess("excel")!;
var word = applications.ForProcess("WINWORD")!;
var league = applications.ForProcess("League of Legends")!;
const string realmDialogue = "Shadow Realm entered. Switching to Shadow Realm operational mode...";
const string coloredTutorial = "The Shotgun boasts considerable destructive power. In the Shadow Realm, fire the Shotgun at the fissures on Blightkristall to destroy them.";
var glossaryCases = new (string Process, string English, string Chinese)[]
{
    ("YuanShen", "Lawrence", "劳伦斯"), ("GenshinImpact", "Knights of Favonius", "西风骑士团"),
    ("YuanShen", "Knight of Favonius", "西风骑士"),
    ("StarRail", "Light Cone", "光锥"), ("ZenlessZoneZero", "W-Engine", "音擎"),
    ("Minecraft.Windows", "Ender Pearl", "末影珍珠"), ("LeagueClientUx", "Smite", "惩戒"),
    ("VALORANT-Win64-Shipping", "Spike", "爆能器"), ("EXCEL", "PivotTable", "数据透视表"),
    ("WINWORD", "Mail Merge", "邮件合并"), ("POWERPNT", "Slide Master", "幻灯片母版"),
    ("PBIDesktop", "Measure", "度量值"), ("Code", "Command Palette", "命令面板"),
    ("devenv", "Solution Explorer", "解决方案资源管理器"), ("blender", "Object Mode", "物体模式"),
    ("Photoshop", "Layer Mask", "图层蒙版"), ("obs64", "Replay Buffer", "回放缓存"),
    ("StarRail", "Self-Modeling Resin", "自塑尘脂"), ("ZenlessZoneZero", "Mindscape Cinema", "意象影画"),
    ("Minecraft.Windows", "Swift Sneak", "迅捷潜行"), ("LeagueClientUx", "Aatrox", "亚托克斯"),
    ("LeagueClientUx", "the Darkin Blade", "暗裔剑魔"), ("LeagueClientUx", "Infinity Edge", "无尽之刃"),
    ("VALORANT-Win64-Shipping", "Phantom", "幻影"), ("EXCEL", "Quick Analysis", "快速分析"),
    ("WINWORD", "Repeat Header Rows", "重复标题行"), ("POWERPNT", "Animation Painter", "动画刷"),
    ("PBIDesktop", "Unpivot Other Columns", "逆透视其他列"), ("Code", "Stage Changes", "暂存更改"),
    ("devenv", "Data Breakpoint", "数据断点"), ("blender", "Merge by Distance", "按距离合并"),
    ("Photoshop", "Object Selection Tool", "对象选择工具"), ("obs64", "Audio Monitoring", "音频监听")
};

using (var translator = new Translator())
{
    foreach (var (process, english, chinese) in glossaryCases)
    {
        var glossary = applications.ForProcess(process);
        Check($"{process} supported mode", "True", applications.IsSupported(process).ToString());
        var selection = SelectWords(english, glossary);
        Check($"{process} hover", chinese, selection?.Term);
        var translation = await translator.TranslateAsync(new Selection(english, english, RectangleF.Empty),
            true, glossary, null, default);
        Check($"{process} whole term", chinese, translation.Text);
        Check($"{process} source", glossary?.Source, translation.Source);
    }

    var demo = applications.ForProcess("ScreenTranslator", isDemoWindow: true);
    var demoTitle = SelectWords("Adventurers' Guild", demo);
    Check("demo uses Genshin glossary", "Genshin", demo?.Id);
    Check("demo title uses offline glossary", "冒险家协会", demoTitle?.Term);
    var demoParagraph = "Investigate the Ley Line Disorder near Stormterror's Lair. Then return to Mondstadt and report what you discovered.";
    Check("demo paragraph glossary", "地脉异常,风龙废墟,蒙德",
        string.Join(',', demo?.Find(demoParagraph).Select(hit => hit.Chinese) ?? []));
    Check("demo glossary is scoped to the main window", null, applications.ForProcess("ScreenTranslator")?.Id);

    const string greeting = "Ad astra abyssosque!";
    const string greetingTranslation = "向着星辰与深渊！";
    foreach (var line in new[] { greeting, "Ad astra abyssosque" })
    {
        var greetingParagraph = await translator.TranslateAsync(new Selection(line, "", RectangleF.Empty),
            true, genshin, null, default);
        Check($"Genshin greeting paragraph {line}", greetingTranslation, greetingParagraph.Text);
        Check($"Genshin greeting offline source {line}", genshin.Source, greetingParagraph.Source);
    }
    Check("Genshin greeting leaves word selection unchanged", "abyssosque", SelectWords(greeting, genshin, 230)?.Text);
    Check("Genshin greeting has no standalone abyssosque term", null, genshin.FindParagraphLabel("abyssosque"));
    Check("Genshin greeting glossary isolation", null, league.FindParagraphLabel(greeting));
}
Check("unknown app", null, applications.ForProcess("chrome")?.Id);
Check("unsupported app in supported mode", "False", applications.IsSupported("chrome").ToString());
Check("GitHub Desktop uses general translation", null, applications.ForProcess("GitHubDesktop")?.Id);
Check("Steam uses general translation", null, applications.ForProcess("steam")?.Id);
Check("Minecraft Java", "Minecraft", applications.ForProcess("javaw", "Minecraft 1.21.4")?.Id);
Check("Minecraft Java in supported mode", "True", applications.IsSupported("javaw", "Minecraft 1.21.4").ToString());
Check("other Java app", null, applications.ForProcess("javaw", "Build settings")?.Id);
Check("other Java app in supported mode", "False", applications.IsSupported("javaw", "Build settings").ToString());
Check("unknown app term", null, SelectWords("Lawrence", null)?.Term);
Check("Genshin term isolation", null, SelectWords("Lawrence", league)?.Term);
Check("Office term isolation", null, SelectWords("Mail Merge", excel)?.Term);
Check("return to Genshin", "劳伦斯", SelectWords("Lawrence", genshin)?.Term);
Check("use term inside paragraph", "数据透视表", excel.Find("Insert a PivotTable in this sheet.").Single().Chinese);
Check("match both instances of the singular Genshin term", "影域,影域", string.Join(',', genshin.Find(realmDialogue).Select(hit => hit.Chinese)));
Check("keep the plural Genshin term", "影域", genshin.Find("Shadow Realms").SingleOrDefault()?.Chinese);
Check("keep the Shadow Realm quest title", "影域的遗留", genshin.Find("Remnants of the Shadow Realm").SingleOrDefault()?.Chinese);
Check("keep the Genshin term scoped to its app", "0", league.Find(realmDialogue).Count().ToString());
Check("common preposition", "in", SelectWords("in", genshin)?.Text);
Check("common phrase", "in front of", SelectWords("in front of the gate", null)?.Text);
Check("preposition sense", "离线释义", SelectWords("in", null)?.TermSource);
using (var translator = new Translator())
{
    Check("ECDICT phonetic transcription", "ˈæpl", translator.FindPronunciation("apple"));
    Check("ECDICT phonetic lookup ignores casing and surrounding spaces", "ˈæpl", translator.FindPronunciation(" APPLE "));
    Check("missing IPA is omitted", null, translator.FindPronunciation("unlisted-test-headword"));
    foreach (var (english, chinese) in new[] { ("survive", "活下来"), ("Foreman", "工头"), ("café", "咖啡"), ("survived", "幸存") })
    {
        var selected = new Selection(english, english, RectangleF.Empty);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var meaning = await translator.TranslateAsync(selected, false, null, null, deadline.Token);
        Check($"{english} ECDICT definition", "True", meaning.Text.Contains(chinese, StringComparison.Ordinal).ToString());
        Check($"{english} ECDICT source", OfflineDictionary.Source, meaning.Source);
    }
    var champion = SelectWords("Flash", league)!;
    var spell = await translator.TranslateAsync(champion, false, league, null, default);
    Check("application term takes precedence over ECDICT", "闪现", spell.Text);
    Check("application term retains source", league.Source, spell.Source);
}
Check("new League items stay in League", null, SelectWords("Infinity Edge", genshin)?.Term);
var valorant = applications.ForProcess("VALORANT-Win64-Shipping")!;
Check("ambiguous weapon name stays a label", "0", valorant.Find("A phantom appeared.").Count().ToString());
var sharedText = new Selection("Select a range.", "", RectangleF.Empty);
var keys = new[] { excel, word, league, genshin, null }.Select(
    glossary => Translator.CacheKey(sharedText, true, glossary, false, [])).ToHashSet();
Check("cache is isolated by app glossary", "5", keys.Count.ToString());
var baiduCredentials = new BaiduCredentials("fixture-app", "fixture-secret");
Check("Baidu signs UTF-8 text before form encoding", "ec21681ff09f32cb68fb9ea2f21fa9df", baiduCredentials.Sign("Ice & stone.", "1234"));
using (var response = JsonDocument.Parse("""{"from":"en","to":"zh","trans_result":[{"dst":"第一段。"},{"dst":"第二段。"}]}"""))
    Check("Baidu response preserves line order", "第一段。\n第二段。", Translator.ParseBaidu(response.RootElement));
using (var response = JsonDocument.Parse("""{"error_code":"52003","error_msg":"UNAUTHORIZED USER"}"""))
{
    try
    {
        Translator.ParseBaidu(response.RootElement);
        Check("Baidu authorization errors point to credentials", "authentication error", "no error");
    }
    catch (TranslationAuthenticationException)
    {
        Check("Baidu authorization errors point to credentials", "authentication error", "authentication error");
    }
}

using (var response = JsonDocument.Parse("""
    {"responseStatus":200,"responseData":{"translatedText":"mandor"},"matches":[
      {"segment":"foreman","target":"zh-CN","translation":"mandor"},
      {"segment":"old foreman","target":"zh-CN","translation":"老工头"},
      {"segment":"foreman","target":"ja-JP","translation":"現場監督"},
      {"segment":"foreman","target":"zh-CN","translation":"n.工头 ,领班,陪审团主席,陪审长"}
    ]}
    """))
{
    Check("MyMemory picks a Chinese match for the exact word", "n.工头 ,领班,陪审团主席,陪审长",
        Translator.ParseMyMemory(response.RootElement, "Foreman"));
    try
    {
        Translator.ParseMyMemory(response.RootElement, "the foreman");
        Check("MyMemory skips unrelated segments", "no translation", "wrong segment");
    }
    catch (NoTranslationException)
    {
        Check("MyMemory skips unrelated segments", "no translation", "no translation");
    }
}

using (var response = JsonDocument.Parse("""
    {"responseStatus":200,"responseData":{"translatedText":"耐 耐 [nai4]"},"matches":[
      {"segment":"survive","target":"zh-CN","translation":"耐 耐 [nai4]"},
      {"segment":"survive","target":"zh-CN","translation":"活下来，幸存"}
    ]}
    """))
{
    Check("MyMemory skips raw pinyin entries", "活下来，幸存", Translator.ParseMyMemory(response.RootElement, "survive"));
}

var cachePath = Path.Combine(Path.GetTempPath(), $"screen-translator-translation-{Guid.NewGuid():N}.json");
try
{
    var cache = new TranslationCache(cachePath);
    var hello = new Selection("qzxqzxunlisted", "qzxqzxunlisted", RectangleF.Empty);
    var goodbye = new Selection("cached farewell", "cached farewell", RectangleF.Empty);
    var apple = new Selection("apple", "apple", RectangleF.Empty);
    await cache.StoreAsync(Translator.CacheKey(hello, false, null, false, []), new TranslationResult("你好"));
    await cache.StoreAsync(Translator.CacheKey(goodbye, false, null, false, []), new TranslationResult("再见", "MyMemory"));
    await cache.StoreAsync(Translator.CacheKey(goodbye, false, null, false, [], OnlineEngine.Baidu), new TranslationResult("下次见", "百度翻译"));
    await cache.StoreAsync(Translator.CacheKey(apple, true, null, false, []), new TranslationResult("苹果。", "MyMemory"));
    var inspect = new Selection("Inspect the settings", "Inspect the settings", RectangleF.Empty);
    var save = new Selection("Save the file", "Save the file", RectangleF.Empty);
    await cache.StoreAsync(Translator.CacheKey(inspect, true, null, false, []), new TranslationResult("检查设置", "Google"));
    await cache.StoreAsync(Translator.CacheKey(save, true, null, false, []), new TranslationResult("保存文件", "Google"));
    using var translator = new Translator(cachePath);
    var legacy = await translator.TranslateAsync(hello, false, null, null, default);
    var myMemory = await translator.TranslateAsync(goodbye, false, null, null, default);
    Check("legacy cache displays original Google source", "Google", legacy.Source);
    Check("cache preserves MyMemory source", "MyMemory", myMemory.Source);
    var baidu = await translator.TranslateAsync(goodbye, false, null, null, default, baiduCredentials, OnlineEngine.Baidu);
    Check("Baidu uses its own online cache", "百度翻译", baidu.Source);
    var paragraph = await translator.TranslateAsync(apple, true, null, null, default);
    Check("paragraph mode skips ECDICT", "MyMemory", paragraph.Source);
    const string sourceList = "/inspect - Inspect the settings\n2. Save the file\n/mnt/project/src";
    var translatedList = await translator.TranslateAsync(new Selection(sourceList, sourceList, RectangleF.Empty), true, null, null, default);
    Check("structured items preserve commands, numbering, paths, and line breaks",
        "/inspect - 检查设置\n2. 保存文件\n/mnt/project/src", translatedList.Text);
}
finally
{
    File.Delete(cachePath);
    File.Delete(cachePath + ".tmp");
}

const string quest = "Use the lamp crystal to light up the secret message";
await TestFixture("world-map", new (int X, int Y, string Name, string Chinese)[]
{
    (451, 483, "Stormterror's Lair", "风龙废墟"), (802, 484, "Stormterror's Lair", "风龙废墟"),
    (1160, 504, "Brightcrown Canyon", "明冠峡"), (1575, 509, "Brightcrown Canyon", "明冠峡"),
    (1854, 604, "Cider Lake", "果酒湖"), (2049, 604, "Cider Lake", "果酒湖"),
    (2312, 434, "Stormbearer Mountains", "望风山地"), (2789, 436, "Stormbearer Mountains", "望风山地"),
    (3030, 263, "Stormbearer Point", "望风角"), (3403, 263, "Stormbearer Point", "望风角"),
    (2510, 686, "Starfell Lake", "星落湖"), (2729, 689, "Starfell Lake", "星落湖"),
    (2995, 750, "Starsnatch Cliff", "摘星崖"), (3300, 751, "Starsnatch Cliff", "摘星崖"),
    (2180, 868, "Whispering Woods", "低语森林"), (2536, 868, "Whispering Woods", "低语森林"),
    (1823, 933, "Mondstadt", "蒙德"), (2025, 934, "Mondstadt", "蒙德"),
    (2968, 1042, "Thousand Winds Temple", "千风神殿"), (3288, 1049, "Thousand Winds Temple", "千风神殿"),
    (3105, 1097, "Thousand Winds Temple", "千风神殿"),
    (1258, 1298, "Wolvendom", "奔狼领"), (1478, 1299, "Wolvendom", "奔狼领"),
    (2429, 1496, "Windrise", "风起地"), (2594, 1497, "Windrise", "风起地"),
    (1850, 1621, "Springvale", "清泉镇"), (2042, 1621, "Springvale", "清泉镇"),
    (1232, 1762, "Dawn Winery", "晨曦酒庄"), (1489, 1762, "Dawn Winery", "晨曦酒庄"),
    (3148, 1782, "Falcon Coast", "鹰翔海滩"), (3384, 1786, "Falcon Coast", "鹰翔海滩"),
    (3242, 2004, "Liffey Region", "黎翡区"), (3541, 2003, "Liffey Region", "黎翡区")
}.SelectMany(p => new[] { new Sample(p.X, p.Y, false, p.Name, p.Chinese), new Sample(p.X, p.Y, true, p.Name, p.Chinese) }));

var questPoints = new (int X, int Y, string Word)[]
{
    (191, 503, "Use"), (352, 503, "lamp"), (458, 504, "crystal"), (545, 503, "crystal"),
    (688, 508, "light"), (909, 504, "secret"), (238, 551, "message")
};
foreach (var file in new[] { "quest-rock", "quest-crystals" })
    await TestFixture(file, questPoints.SelectMany(p => new[]
    {
        new Sample(p.X, p.Y, false, p.Word), new Sample(p.X, p.Y, true, quest)
    }));

const string subtitle = """But from the "master's" perspective, researching the treeheart was no sin, since it was only to find a way to completely eradicate the Blightkristall scourge.""";
const string optionOne = "A solution to the crisis had to be found...";
const string optionTwo = "Not like you can stand around in the face of danger...";
await TestFixture("dialogue", new (int X, int Y, string Word, string Paragraph)[]
{
    (765, 1824, "But", subtitle), (1367, 1821, "master's", subtitle), (1704, 1823, "perspective", subtitle),
    (2570, 1825, "treeheart", subtitle), (2310, 1902, "eradicate", subtitle),
    (2880, 1899, "Blightkristall", subtitle), (1930, 1978, "scourge", subtitle),
    (2800, 1395, "solution", optionOne), (2760, 1460, "found", optionOne),
    (3340, 1545, "around", optionTwo), (2770, 1610, "face", optionTwo), (1920, 1710, "Glinin", "Glinin")
}.SelectMany(p => new[] { new Sample(p.X, p.Y, false, p.Word), new Sample(p.X, p.Y, true, p.Paragraph) }));

const string upperNote = "What transpired here has, if nothing else, answered some questions long left unresolved. Everyone knows that in the wake of Hyperborea's fall... Owing to (...) within the ley lines, an unchecked (...) devoured all warmth, turning into pitch-black Blightkristall and leaving the northern lands a frozen waste. The various fae kindreds that survived all possessed the strength to weather the Kristall-Blight and the tempests. So how did we ever survive, a kind so small of stature, surpassing humankind only in spirit and perception?";
const string lowerNote = "I had once reckoned my forced departure from Snezhnograd to be a bitter loss. I have also heard what that fellow Izmaylov has been plotting. If he truly gets his way, it will only mean endless friction at the Veche hereafter. Still... to think that I should uncover the secrets of this place. It was all well worth it.";
await TestFixture("long-note", Paragraphs(upperNote, (1120, 275), (1630, 449), (2120, 607), (1550, 855), (1460, 1022))
    .Concat(Paragraphs(lowerNote, (1160, 1360), (2130, 1530), (1500, 1850))));

const string tutorial = """A device created by the ancient fae that can alter the path of luminous light. Refraction mechanisms activate when illuminated by a "Luminous Beam" and can be used to freely redirect the path of the light.""";
await TestFixture("tutorial-columns", Paragraphs(tutorial, (1910, 1424), (2930, 1423), (2010, 1480),
    (2330, 1543), (2350, 1603), (2060, 1650)).Concat([
        new Sample(2420, 1320, true, "Refraction Mechanisms"),
        new Sample(554, 1460, true, "Prime Ice Construct: Pickaxe"),
        new Sample(500, 1700, true, "About Hydro")
    ]));

await TestFixture("achievements", Paragraphs("Challenger: Series IV", (2810, 882), (2891, 882),
        (2935, 880), (3004, 883), (2860, 943), (3025, 944))
    .Concat(Paragraphs("Challenger: Series II", (1797, 881), (1840, 943)))
    .Concat(Paragraphs("Challenger: Series VI", (310, 1598), (340, 1653)))
    .Concat(new[] { (2810, 882), (2891, 882), (2935, 880), (3004, 883), (1797, 881), (310, 1598) }
        .Select(p => new Sample(p.Item1, p.Item2, false, "Challenger"))));

await TestFixture("npc-name", new[] { 2390, 2440, 2490, 2540, 2600 }.SelectMany(x => new[]
{
    new Sample(x, 425, false, "Lawrence"), new Sample(x, 425, true, "Lawrence")
}).Concat(Paragraphs("<Knight of Favonius>", (2450, 480), (2580, 485))));

await TestFixture("dark-text-ice-you", [
    new Sample(1000, 338, false, "you"), new Sample(1038, 339, false, "you"),
    new Sample(1018, 346, false, "you"), new Sample(930, 339, false, "with")
]);
await TestFixture("dark-text-ice-domovoy", [
    new Sample(695, 73, false, "Domovoy"), new Sample(718, 73, false, "Domovoy"),
    new Sample(737, 73, false, "Domovoy"), new Sample(774, 69, false, "Domovoy"),
    new Sample(921, 71, false, "for")
]);
await TestFixture("domovoy-foreman", [new Sample(2065, 166, false, "foreman"), new Sample(2250, 166, false, "foreman")]);
await TestFixture("dialogue-survive", [
    new Sample(1020, 268, false, "survive"),
    new Sample(1110, 268, false, "survive"),
    new Sample(1193, 268, false, "survive")
]);
await TestFixture("dialogue-shadow-realm", Paragraphs(realmDialogue, (365, 273), (1390, 270)));
await TestFixture("tutorial-colored-words", Paragraphs(coloredTutorial, (541, 65), (980, 136), (1500, 138), (2010, 137), (230, 209))
    .Concat([new Sample(260, 72, false, "Shotgun"), new Sample(1500, 138, false, "fissures")]));
var headedTutorial = Paragraphs(coloredTutorial, (705, 156), (1250, 217), (704, 270))
    .Concat(Paragraphs("Blightkristall Fissures...", (780, 52)));
await TestFixture("tutorial-colored-heading", headedTutorial, [1.0, 0.75, 0.5]);
await TestFixture("tutorial-colored-heading", headedTutorial, [1.0, 0.75, 0.5], dpi: 2f);
const string cyanDialogue = "Just a touch of drowsiness... It's nothing, really. There was indeed a time "
    + "when I completely loathed humans. And yet, for some reason, ever since a certain day, I simply stopped finding them so distasteful.";
var dialogueWithCyan = Paragraphs(cyanDialogue, (990, 439), (1700, 517), (2165, 529), (2440, 527), (540, 602), (1870, 598));
await TestFixture("dialogue-cyan-emphasis", dialogueWithCyan, [1.0, 0.75, 0.5]);
await TestFixture("dialogue-cyan-emphasis", dialogueWithCyan, [1.0, 0.75, 0.5], dpi: 2f);
var stackedChoices = Paragraphs("You seem a little drained, Vodyanitsa.", (500, 106), (575, 172), (975, 110))
    .Concat(Paragraphs("Vodyanitsa looks like she's been out of the water too long...", (500, 257), (700, 320), (1270, 252)));
stackedChoices = stackedChoices.Select(sample => sample with { Chinese = "沃雅妮莎" });
await TestFixture("dialogue-stacked-options", stackedChoices, [1.0, 0.75, 0.5]);
await TestFixture("dialogue-stacked-options", stackedChoices, [1.0, 0.75, 0.5], dpi: 2f);
var viewerChoices = Paragraphs("You seem a little drained, Vodyanitsa.", (390, 110), (410, 94), (575, 172), (687, 189), (975, 110))
    .Concat(Paragraphs("Vodyanitsa looks like she's been out of the water too long...",
        (390, 250), (410, 240), (500, 257), (700, 320), (1270, 252), (1332, 253)))
    .Concat([
        new Sample(331, 137, true, null), new Sample(332, 286, true, null),
        new Sample(419, 108, false, "You"), new Sample(1010, 112, false, "drained"),
        new Sample(530, 172, false, "Vodyanitsa"), new Sample(500, 254, false, "Vodyanitsa"),
        new Sample(863, 253, false, "like"), new Sample(1304, 255, false, "out"),
        new Sample(510, 319, false, "the"), new Sample(910, 320, false, "long"),
        new Sample(331, 137, false, null), new Sample(332, 286, false, null)
    ]);
await TestFixture("dialogue-stacked-options", viewerChoices, [0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5], game: false);
await TestFixture("dialogue-stacked-options", viewerChoices, [1.25, 1.5, 2.0, 2.5]);
const string explainDialogue = "(You explain what happened to Koshkin...)";
await TestFixture("dialogue-explain", Paragraphs(explainDialogue, (420, 91), (490, 93), (567, 95), (640, 95), (340, 153))
    .Concat([new Sample(420, 91, false, "explain"), new Sample(490, 93, false, "explain"), new Sample(567, 95, false, "explain")]),
    [1.0, 0.75, 0.5, 1.5]);

foreach (var (fontName, style, ink, background) in new[]
{
    ("Segoe UI", FontStyle.Regular, Color.Black, Color.White),
    ("Georgia", FontStyle.Italic, Color.DarkBlue, Color.PapayaWhip),
    ("Palatino Linotype", FontStyle.Italic, Color.Gold, Color.DarkSlateBlue)
})
{
    using var bitmap = new Bitmap(900, 220, PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(bitmap))
    using (var font = new Font(fontName, 29, style, GraphicsUnit.Pixel))
    using (var brush = new SolidBrush(ink))
    {
        graphics.Clear(background);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.DrawString("Follow the winding path", font, brush, 55, 65);
        graphics.DrawString("and open the gate.", font, brush, 55, 103);
    }
    await TestRenderedZooms(fontName, bitmap, [new Sample(87, 83, true, "Follow the winding path and open the gate.")]);
}

using (var frame = new Bitmap(920, 360, PixelFormat.Format32bppArgb))
using (var graphics = Graphics.FromImage(frame))
using (var desktopFont = new Font("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel))
using (var mixedFont = new Font("Microsoft YaHei UI", 23, FontStyle.Regular, GraphicsUnit.Pixel))
{
    graphics.Clear(Color.FromArgb(33, 65, 91));
    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    graphics.DrawString("GitHub", desktopFont, Brushes.Black, 51, 81);
    graphics.DrawString("Desktop", desktopFont, Brushes.Black, 47, 101);
    graphics.DrawString("GitHub", desktopFont, Brushes.White, 50, 80);
    graphics.DrawString("Desktop", desktopFont, Brushes.White, 46, 100);
    graphics.FillRectangle(Brushes.White, 340, 60, 500, 120);
    graphics.DrawString("打开Desktop设置", mixedFont, Brushes.Black, 363, 108);
    await TestRenderedZooms("desktop icon", frame, new[] { 51, 77, 100 }.Select(x => new Sample(x, 108, false, "Desktop")), game: false);
    await TestRenderedZooms("English between Chinese characters", frame,
        new[] { 418, 452, 494 }.Select(x => new Sample(x, 121, false, "Desktop")), game: false);
}

using (var desktop = new Bitmap(Path.Combine(fixtureRoot, "desktop-icon.png")))
{
    await TestRenderedZooms("actual desktop icon", desktop,
        new[] { 37, 72, 106 }.Select(x => new Sample(x, 150, false, "Desktop")), game: false);
}
using (var mixedText = new Bitmap(Path.Combine(fixtureRoot, "mixed-text.png")))
{
    await TestRenderedZooms("actual mixed text", mixedText,
        new[] { 91, 139, 182 }.Select(x => new Sample(x, 40, false, "hatch-pet")).Append(new Sample(52, 40, false, null)),
        game: false);
}
var terminalWord = new[] { 355, 430, 457, 495, 525 }.Select(x => new Sample(x, 66, false, "sites-remix"));
await TestFixture("mixed-terminal-hyphen", terminalWord, [1.0], dpi: 1.75f, game: false);

ScreenLine LayoutLine(int x, int y, int width, string text)
{
    var bounds = new RectangleF(x, y, width, 20);
    return new ScreenLine([new ScreenWord(text, bounds)], bounds);
}

ScreenLine[] commands =
[
    LayoutLine(10, 10, 55, "/open"), LayoutLine(90, 10, 260, "Open the current workspace"),
    LayoutLine(10, 36, 55, "[save"), LayoutLine(90, 36, 260, "Save any pending changes"),
    LayoutLine(10, 62, 65, "/close"), LayoutLine(90, 62, 260, "Close the active document"),
    LayoutLine(420, 36, 140, "Unrelated sidebar")
];
const string commandItems = "/open - Open the current workspace\n/save - Save any pending changes\n/close - Close the active document";
Check("command column selects complete items", commandItems, TextSelection.At(commands, new Point(20, 43), true, null)?.Text);
Check("description column selects the same items", commandItems, TextSelection.At(commands, new Point(180, 43), true, null)?.Text);
Check("a nearby sidebar stays independent", "Unrelated sidebar", TextSelection.At(commands, new Point(450, 43), true, null)?.Text);
Check("normal paragraph keeps its sentence across a visual wrap", "The sentence continues onto the next line in the same paragraph.",
    TextSelection.At([LayoutLine(10, 10, 250, "The sentence continues onto the"),
        LayoutLine(10, 36, 238, "next line in the same paragraph.")], new Point(40, 43), true, null)?.Text);
ScreenLine[] numbered =
[
    LayoutLine(10, 10, 15, "1."), LayoutLine(36, 10, 250, "Install the desktop application"),
    LayoutLine(36, 36, 240, "and open the project."), LayoutLine(10, 62, 15, "2."),
    LayoutLine(36, 62, 220, "Review the configuration.")
];
const string numberedItems = "1. Install the desktop application and open the project.\n2. Review the configuration.";
Check("an indented numbered-list continuation joins its own item", numberedItems,
    TextSelection.At(numbered, new Point(100, 21), true, null)?.Text);
Check("hovering an indented continuation selects the complete list", numberedItems,
    TextSelection.At(numbered, new Point(100, 45), true, null)?.Text);
Check("aligned key-value rows stay separate", "Build ID: 128\nTarget: Windows",
    TextSelection.At([LayoutLine(20, 10, 150, "Build ID: 128"), LayoutLine(20, 36, 170, "Target: Windows")],
        new Point(80, 46), true, null)?.Text);

const string slashLayout = "/create an AGENTS.md file with instructions for Codex\n/show current session configuration\n"
    + "/choose what Codex is allowed to do\n/choose what model and reasoning effort to use\n/review any changes and find issues";
await TestFixture("terminal-command-list", new[] { 62, 530 }.Select(x =>
    new Sample(x, 75, true, slashLayout, Project: CommandDescriptions)), [1.0], game: false);
await TestFixture("terminal-command-list-bracket", new[] { 65, 500 }.Select(x =>
    new Sample(x, 95, true, slashLayout, Project: CommandDescriptions)), [1.0], game: false);
const string statusLines = "OpenAI Codex\n/mnt/d/GitDesktopRep\npermissions: YOLO mode";
await TestFixture("terminal-status-header", new[] { 47, 77, 112 }.Select(y =>
    new Sample(164, y, true, statusLines)), [1.0], game: false);

string? CommandDescriptions(string? text) => text is null ? null : string.Join('\n', text.Split('\n').Select(item =>
    item.StartsWith('/') && item.IndexOf(" - ", StringComparison.Ordinal) is > 0 and var separator
        ? "/" + item[(separator + 3)..].Replace("AGENTS. md", "AGENTS.md", StringComparison.Ordinal) : item));

await TestDesktopText("mixed-sidebar", [
    new(198, 169, false, "Markdown"), new(250, 169, false, "Markdown"), new(297, 170, false, "Markdown"),
    new(120, 333, false, "codex"), new(158, 333, false, "codex"), new(319, 333, false, "env"),
    new(100, 496, false, "Trump"), new(147, 496, false, "Trump"),
    new(101, 603, false, "WSL"), new(121, 603, false, "WSL"), new(139, 603, false, "WSL"),
    new(156, 603, false, "chdir"), new(197, 603, false, "chdir"),
    new(99, 657, false, "hatch-pet"), new(189, 657, false, "hatch-pet"),
    new(110, 1093, false, "bilibili"), new(165, 1093, false, "bilibili")
]);
await TestDesktopText("mixed-sidebar-codex", [
    new(110, 1129, false, "Codex"), new(119, 1129, false, "Codex"), new(131, 1129, false, "Codex"),
    new(144, 1129, false, "Codex"), new(155, 1129, false, "Codex"), new(168, 1129, false, "Codex"),
    new(117, 640, false, null), new(126, 640, false, "codex"), new(130, 640, false, "codex"), new(148, 640, false, "codex"),
    new(180, 640, false, "codex"), new(155, 96, false, "Dot"), new(109, 230, false, "mine"),
    new(201, 477, false, "Markdown"), new(260, 477, false, "Markdown"), new(307, 478, false, "Markdown"),
    new(334, 640, false, "env"), new(116, 910, false, "WSL"), new(183, 910, false, "chdir"),
    new(118, 969, false, "hatch-pet"), new(189, 969, false, "hatch-pet")
], canvasBackground: Color.FromArgb(248, 248, 250), canvasDpi: 1.75f);
using (var sidebar = new Bitmap(Path.Combine(fixtureRoot, "mixed-sidebar-codex.png")))
{
    foreach (var zoom in Zooms(.75, 1.25, 1.5, 1.75))
    {
        using var frame = new Bitmap(1920, (int)(sidebar.Height * zoom) + 120, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(frame))
        {
            graphics.Clear(Color.FromArgb(248, 248, 250));
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(sidebar, new Rectangle(280, 60, (int)(sidebar.Width * zoom), (int)(sidebar.Height * zoom)),
                0, 0, sidebar.Width, sidebar.Height, GraphicsUnit.Pixel);
        }
        foreach (var point in new Sample[] { new(110, 1126, false, "Codex"), new(144, 1129, false, "Codex"),
                     new(168, 1133, false, "Codex"), new(127, 640, false, "codex"), new(183, 640, false, "codex"),
                     new(204, 477, false, "Markdown"), new(304, 477, false, "Markdown") })
            await TestSample($"mixed-sidebar-codex at {zoom:P0}", frame, point with
            {
                X = 280 + (int)Math.Round(point.X * zoom), Y = 60 + (int)Math.Round(point.Y * zoom)
            }, 1, game: false, dpi: 1.75f);
    }
}
await TestDesktopText("mixed-filename", [
    new(186, 64, false, "ScreenTranslator"), new(305, 64, false, "ScreenTranslator"),
    new(384, 64, false, "ScreenTranslator"), new(431, 64, false, "exe")
]);
await TestDesktopText("muted-mixed-text", [
    new(221, 173, false, "AdGuard"), new(260, 173, false, "AdGuard"), new(307, 173, false, "AdGuard"),
    new(326, 173, false, "AdBlock"), new(367, 173, false, "AdBlock"), new(408, 173, false, "AdBlock")
], canvasBackground: Color.White);
using (var muted = new Bitmap(Path.Combine(fixtureRoot, "muted-mixed-text-followup.png")))
{
    foreach (var zoom in Zooms(0.75, 1.0, 1.25, 1.5, 2.0))
    {
        using var viewer = new Bitmap(1920, 850, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(viewer))
        {
            graphics.Clear(Color.White);
            graphics.DrawImage(muted, new Rectangle(480, 80, (int)(muted.Width * zoom), (int)(muted.Height * zoom)));
        }
        foreach (var (from, to, expectedWord) in new[] { (205, 300, "AdGuard"), (315, 399, "AdBlock") })
        {
            for (var x = from; x <= to; x += 8)
                await TestSample($"muted-mixed-text-followup {zoom:P0}", viewer,
                    new Sample(480 + (int)(x * zoom), 80 + (int)(168 * zoom), false, expectedWord), 1, game: false, dpi: 1.75f);
        }
    }
}
Sample[] hdrNotice =
[
    new(235, 188, false, "AdGuard"), new(275, 188, false, "AdGuard"), new(315, 188, false, "AdGuard"),
    new(338, 188, false, "AdBlock"), new(379, 188, false, "AdBlock"), new(419, 188, false, "AdBlock")
];
foreach (var dpi in new[] { 1f, 1.75f, 2f })
    await TestFixture("muted-mixed-text-hdr", hdrNotice, [1.0], dpi: dpi, game: false);
await TestDesktopText("heavy-display-text", [
    new(55, 74, false, "BLUE"), new(85, 74, false, "BLUE"),
    new(101, 74, false, "SIDE"), new(124, 74, false, "SIDE"),
    new(338, 74, false, "PICK"), new(364, 74, false, "PICK")
], canvasBackground: Color.Black);
using (var displaySample = new Bitmap(Path.Combine(fixtureRoot, "heavy-display-text.png")))
{
    var hoverPoints = new Sample[]
    {
        new(98, 65, false, "SIDE"), new(118, 74, false, "SIDE"), new(129, 81, false, "SIDE"),
        new(335, 65, false, "PICK"), new(350, 74, false, "PICK"), new(368, 81, false, "PICK")
    };
    foreach (var zoom in Zooms(0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0))
    {
        using var window = new Bitmap(3840, 900, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(window))
        {
            graphics.Clear(Color.FromArgb(32, 32, 32));
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(displaySample, new Rectangle(1130, 236, (int)Math.Round(displaySample.Width * zoom),
                (int)Math.Round(displaySample.Height * zoom)));
        }
        foreach (var point in hoverPoints)
            await TestSample($"display at {zoom:P0}", window, point with
            {
                X = 1130 + (int)Math.Round(point.X * zoom), Y = 236 + (int)Math.Round(point.Y * zoom)
            }, 1, game: false, dpi: 1.75f);
    }
}
await TestDesktopText("desktop-grid", [
    new(325, 126, false, "Chrome"), new(457, 99, false, "SteamVR"),
    new(324, 299, false, "Edge"), new(455, 470, false, "Secretary"),
    new(311, 612, false, "Everything"), new(458, 982, false, "Desktop"),
    new(198, 1495, false, "Desktop"), new(458, 1638, false, "Room")
]);
Sample[] logoPoints = [
    new(85, 163, false, "durex"), new(228, 161, false, "durex"),
    new(363, 157, false, "durex"), new(449, 157, false, "durex"),
    new(478, 217, false, "durex")
];
await TestDesktopText("white-cutout-logo", logoPoints, canvasBackground: Color.White, canvasDpi: 1.75f);
using (var logo = new Bitmap(Path.Combine(fixtureRoot, "white-cutout-logo.png")))
{
    await TestRenderedZooms("outside white-cutout-logo", logo,
        [new(275, 53, false, null), new(522, 217, false, null), new(275, 260, false, null)], game: false);

    foreach (var zoom in Zooms(.5, .75, 1.25))
    {
        using var window = new Bitmap(1920, 680, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(window))
        {
            graphics.Clear(Color.White);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(logo, new Rectangle(600, 100, (int)(logo.Width * zoom), (int)(logo.Height * zoom)));
        }
        foreach (var point in logoPoints)
            await TestSample($"white-cutout-logo at {zoom:P0}", window, point with
            {
                X = 600 + (int)Math.Round(point.X * zoom), Y = 100 + (int)Math.Round(point.Y * zoom)
            }, 1, game: false);
    }
}
Sample[] videoOverlayPoints = [
    new(150, 60, false, "BLG"), new(170, 65, false, "BLG"), new(188, 62, false, "BLG"),
    new(198, 60, false, "Viper"), new(210, 67, false, "Viper"),
    new(230, 62, false, "Viper"), new(248, 61, false, "Viper")
];
await TestDesktopText("hdr-overlay-text", videoOverlayPoints,
    canvasBackground: Color.FromArgb(31, 31, 31), canvasDpi: 1.75f);
await TestDesktopText("hdr-overlay-text-gdi", videoOverlayPoints,
    canvasBackground: Color.FromArgb(31, 31, 31), canvasDpi: 1.75f);
await TestDesktopText("league-scoreboard", [
    new(22, 35, false, "GEN"), new(49, 35, false, "GEN"), new(78, 35, false, "GEN"),
    new(1228, 35, false, "BLG"), new(1256, 35, false, "BLG"), new(1287, 35, false, "BLG"),
    new(20, 85, false, "LCK"), new(43, 85, false, "LCK"), new(64, 85, false, "LCK"),
    new(1208, 85, false, "LPL"), new(1230, 85, false, "LPL"), new(1246, 86, false, "LPL")
], canvasBackground: Color.FromArgb(31, 31, 31), canvasDpi: 1.75f);
using (var scoreboard = new Bitmap(Path.Combine(fixtureRoot, "league-scoreboard.png")))
{
    foreach (var zoom in Zooms(1.25, 1.75))
    {
        using var window = new Bitmap(3840, 540, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(window))
        {
            graphics.Clear(Color.FromArgb(31, 31, 31));
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(scoreboard, new Rectangle(700, 85, (int)Math.Round(scoreboard.Width * zoom),
                (int)Math.Round(scoreboard.Height * zoom)));
        }
        foreach (var (x, y, expectedWord) in new[] { (25, 29, "GEN"), (75, 50, "GEN"), (1235, 28, "BLG"), (1280, 50, "BLG"),
                     (20, 78, "LCK"), (40, 85, "LCK"), (65, 94, "LCK"), (1210, 81, "LPL"), (1244, 89, "LPL") })
        {
            await TestSample($"scoreboard at {zoom:P0}", window,
                new Sample(700 + (int)Math.Round(x * zoom), 85 + (int)Math.Round(y * zoom), false, expectedWord),
                1, game: false, dpi: 1.75f);
        }
    }
}
foreach (var label in new[] { "Google Chrome", "GitHub Desktop", "Docker Desktop", "SteamVR", "Visual Studio Code", "ComfyUI" })
    Check($"Explorer label {label}", label, DesktopLabels.FromName(label, new Rectangle(0, 0, 100, 100))?.Text);
Check("Explorer non-English label", null, DesktopLabels.FromName("回收站", new Rectangle(0, 0, 100, 100))?.Text);

async Task TestDesktopText(string name, Sample[] samples, Color? canvasBackground = null, float canvasDpi = 1)
{
    using var image = new Bitmap(Path.Combine(fixtureRoot, name + ".png"));
    await TestRenderedZooms(name, image, samples, game: false);
    if (canvasBackground is not { } background)
        return;

    foreach (var zoom in Zooms(1))
    {
        var width = (int)(image.Width * zoom);
        var height = (int)(image.Height * zoom);
        using var canvas = new Bitmap(Math.Max((int)(1920 * canvasDpi), width + 701),
            Math.Max(540, height + 170), PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(background);
            graphics.DrawImage(image, new Rectangle(700, 85, width, height),
                0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
        }
        foreach (var sample in samples)
            await TestSample(zoom == 1 ? name + " inside window" : $"{name} inside window at {zoom:P0}", canvas,
                sample with { X = 700 + (int)(sample.X * zoom), Y = 85 + (int)(sample.Y * zoom) }, 1,
                game: false, dpi: canvasDpi);
    }
}

Console.WriteLine($"{checks - failures}/{checks} checks passed.");
return failures == 0 ? 0 : 1;

Selection? SelectWords(string text, TermGlossary? glossary, int hoverX = 30)
{
    var words = text.Split(' ').Select((value, index) =>
        new ScreenWord(value, new RectangleF(index * 100, 20, 80, 20)));
    return TextSelection.At(ScreenOcr.BuildLines(words).ToArray(), new Point(hoverX, 30), false, glossary);
}

IEnumerable<Sample> Paragraphs(string text, params (int X, int Y)[] points) =>
    points.Select(point => new Sample(point.X, point.Y, true, text));

async Task TestFixture(string name, IEnumerable<Sample> samples, double[]? ratios = null, float dpi = 1f, bool game = true)
{
    var path = Path.Combine(fixtureRoot, name + ".png");
    if (!File.Exists(path))
    {
        Check($"fixture {name}", path, null);
        return;
    }
    using var image = new Bitmap(path);
    await TestRenderedZooms(name, image, samples, game, dpi, ratios ?? [1.0, 0.5]);
}

IEnumerable<double> Zooms(params double[] existing) => zoomMatrix ? requiredZooms : existing;

async Task TestRenderedZooms(string name, Bitmap image, IEnumerable<Sample> samples, bool game = true,
    float dpi = 1f, double[]? ratios = null)
{
    foreach (var ratio in Zooms(ratios ?? [1.0]))
    {
        using var resized = ratio == 1 ? null : new Bitmap(image, (int)(image.Width * ratio), (int)(image.Height * ratio));
        foreach (var sample in samples)
            await TestSample(name, resized ?? image, sample, ratio, game: game, dpi: dpi);
    }
}

async Task TestSample(string name, Bitmap frame, Sample sample, double ratio, bool game = true, float dpi = 1)
{
    var cursor = new Point((int)(sample.X * ratio), (int)(sample.Y * ratio));
    var area = new Rectangle(Point.Empty, frame.Size);
    var capture = HoverRecognition.CaptureArea(area, cursor, dpi, sample.Paragraph);
    var wider = sample.Paragraph ? capture : HoverRecognition.CaptureArea(area, cursor, dpi, false, expanded: true);
    var (target, _) = await HoverRecognition.ReadAsync(area, capture, wider, cursor, sample.Paragraph,
        game, game ? genshin : null, Read, default, ReadOption);
    var actual = target is null ? null : game ? genshin.CorrectOcr(target.Text) : target.Text;
    var description = $"{name} {ratio} ({sample.X},{sample.Y}) {(sample.Paragraph ? "paragraph" : "word")}";
    Check(description, sample.Expected, sample.Project is null ? actual : sample.Project(actual));
    if (sample.Chinese is { } chinese && actual is not null && actual == sample.Expected)
    {
        var term = target?.Term ?? genshin.Find(actual).SingleOrDefault()?.Chinese;
        Check($"{description} glossary", chinese, term);
    }

    async Task<ScreenLine[]> Read(Rectangle region, float scale, Point point, Func<ScreenLine[], bool>? hasTarget,
        CancellationToken token, bool allowMixedText, bool largeDisplayOnly, bool paragraph)
    {
        using var bitmap = frame.Clone(region, PixelFormat.Format32bppArgb);
        return await ocr.ReadBitmapAsync(bitmap, region.Location, scale, point, hasTarget, token, allowMixedText, largeDisplayOnly, paragraph);
    }

    async Task<ScreenLine[]?> ReadOption(Rectangle region, Point point, CancellationToken token)
    {
        using var bitmap = frame.Clone(region, PixelFormat.Format32bppArgb);
        return await ocr.ReadOptionBitmapAsync(bitmap, region.Location, point, token);
    }
}

void Check(string subject, string? expected, string? actual)
{
    checks++;
    if (string.Equals(expected, actual, StringComparison.Ordinal))
        return;
    failures++;
    Console.Error.WriteLine($"{subject}\n  expected: {expected ?? "(none)"}\n  actual:   {actual ?? "(none)"}");
}

internal sealed record Sample(int X, int Y, bool Paragraph, string? Expected, string? Chinese = null,
    Func<string?, string?>? Project = null);
