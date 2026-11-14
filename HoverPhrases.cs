using System.Text.RegularExpressions;

namespace ScreenTranslator;

internal static partial class HoverPhrases
{
    private const string LocalSource = "离线释义";
    private static readonly Dictionary<string, string> Meanings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["in"] = "介词  在…里、在…中、在（某时间）、用（某种语言或方式）",
        ["in blue"] = "穿蓝色衣服；用蓝色（标记或书写）",
        ["in front of"] = "在…前面",
        ["in the middle of"] = "在…中间；正在做…",
        ["in the meantime"] = "与此同时；在这期间",
        ["in the end"] = "最后；最终",
        ["in the first place"] = "首先；一开始",
        ["in advance"] = "提前；预先",
        ["in a hurry"] = "匆忙地",
        ["in a row"] = "连续地；排成一排",
        ["in case"] = "以防；万一",
        ["in case of"] = "万一遇到…；如果发生…",
        ["in charge of"] = "负责；掌管",
        ["in exchange for"] = "作为对…的交换",
        ["in fact"] = "事实上",
        ["in need of"] = "需要…",
        ["in order to"] = "为了…",
        ["in return for"] = "作为对…的回报",
        ["in spite of"] = "尽管…",
        ["in terms of"] = "就…而言",
        ["in addition"] = "此外；另外",
        ["in addition to"] = "除…之外还有…",
        ["in touch with"] = "与…保持联系",
        ["in danger"] = "处于危险中",
        ["in trouble"] = "遇到麻烦",
        ["in time"] = "及时；最终",
        ["on time"] = "准时",
        ["on behalf of"] = "代表…",
        ["on purpose"] = "故意地",
        ["on the other hand"] = "另一方面",
        ["by accident"] = "意外地；偶然地",
        ["by the way"] = "顺便说一下",
        ["at least"] = "至少",
        ["at most"] = "至多",
        ["at once"] = "立刻；同时",
        ["all of a sudden"] = "突然",
        ["as a result"] = "因此；结果",
        ["as long as"] = "只要…；长达…",
        ["as soon as"] = "一…就…",
        ["as well as"] = "以及；和…一样好",
        ["according to"] = "根据…；据…所说",
        ["apart from"] = "除了…之外",
        ["because of"] = "因为…",
        ["due to"] = "由于…",
        ["even if"] = "即使…",
        ["even though"] = "尽管…",
        ["instead of"] = "代替…；而不是…",
        ["out of stock"] = "缺货",
        ["out of reach"] = "够不着；达不到",
        ["up to"] = "多达…；取决于…",
        ["take care of"] = "照顾；处理",
        ["take part in"] = "参加；参与",
        ["take place"] = "发生；举行",
        ["make sure"] = "确保；确认",
        ["make up for"] = "弥补…",
        ["keep an eye on"] = "留意；照看",
        ["keep up with"] = "跟上…",
        ["get rid of"] = "摆脱；除去",
        ["get along with"] = "与…相处",
        ["come across"] = "偶然遇见；无意中发现",
        ["figure out"] = "弄清楚；想出",
        ["find out"] = "查明；发现",
        ["look after"] = "照顾；照料",
        ["look for"] = "寻找",
        ["look into"] = "调查；察看",
        ["look forward to"] = "期待；盼望",
        ["run out of"] = "用完；耗尽",
        ["give up"] = "放弃",
        ["carry out"] = "执行；完成",
        ["set out"] = "出发；着手",
        ["set off"] = "出发；引发",
        ["pick up"] = "捡起；接人；学会",
        ["head back"] = "返回；往回走",
        ["watch out for"] = "当心；留意",
        ["used to"] = "过去常常；曾经",
        ["have to"] = "不得不；必须",
        ["ought to"] = "应该",
        ["rather than"] = "而不是…",
        ["no longer"] = "不再"
    };

    private static readonly int LongestPhrase = Meanings.Keys.Max(text => text.Count(letter => letter == ' ') + 1);

    [GeneratedRegex(@"^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$")]
    private static partial Regex OuterPunctuation();

    public static Selection? At(ScreenLine line, int index)
    {
        for (var length = Math.Min(LongestPhrase, line.Words.Length); length > 0; length--)
        {
            var lastStart = Math.Min(index, line.Words.Length - length);
            for (var start = Math.Max(0, index - length + 1); start <= lastStart; start++)
            {
                var end = start + length - 1;
                var text = OuterPunctuation().Replace(string.Join(' ', line.Words.Skip(start).Take(length).Select(word => word.Text)), "");
                if (Meanings.TryGetValue(text, out var translation))
                    return new Selection(text, line.Text, RectangleF.Union(line.Words[start].Bounds, line.Words[end].Bounds), translation, LocalSource);
            }
        }
        return null;
    }
}
