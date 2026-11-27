using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LLPlayer.Services;

internal sealed record AnkiChoice(CloudCard Card, CloudExample Example)
{
    public override string ToString() => $"{Card.Expression} · {Card.Meaning} — {Example.English}（{Example.Source}）";
}

internal sealed record AnkiNotePayload(string Key, string MediaName, byte[] Mp3, string Expression,
    string Meaning, string English, string Source);

internal sealed record AnkiExportResult(string TsvPath, string GuidePath, int CardCount, int MediaCount)
{
    internal string StatusText => $"本地文件：已导出 {CardCount} 张卡、{MediaCount} 个音频。\n" +
        "桌面 Anki：待手动导入（LLPlayer 不读取导入结果）。\n" +
        "AnkiWeb：待手动同步（LLPlayer 不读取同步结果）。";
}

internal static class AnkiExportService
{
    private const int MaxCards = 500;
    internal const string NoteType = "LLPlayer 原声 v1";
    internal const string Deck = "LLPlayer 原声复习";
    internal const string Front = "{{音频}}";
    internal const string Back = "{{FrontSide}}<hr id=answer><b>{{表达}}</b><p>{{本句义}}</p><p>{{英文原句}}</p><p>{{出处}}</p>";

    internal static string Key(CloudCard card) => $"{card.LibraryId:N}:{card.CardId:N}";
    internal static string PauseQuery(CloudCard card) => $"\"ID:{Key(card)}\"";

    internal static IEnumerable<AnkiChoice> Choices(CloudState state) => state.Cards
        .SelectMany(card=>Choices(state.LibraryId,card));

    internal static IEnumerable<AnkiChoice> Choices(Guid libraryId,CloudCard card)
    {
        if (card.LibraryId!=libraryId || card.Status!="active" || card.Reason=="import"
            || HasPath(card.Expression) || HasPath(card.Meaning)) yield break;
        foreach (var example in card.Examples)
            if (example.Verified && !HasPath(example.English) && !HasPath(example.Source))
                yield return new(card,example);
    }

    internal static AnkiNotePayload BuildNote(AnkiChoice choice,Func<string,string> mediaPath)
    {
        var (card,example)=choice;
        var sha=example.Sha256;
        if (!Regex.IsMatch(sha,"\\A[0-9a-f]{64}\\z")) throw new InvalidDataException("原声摘要无效，请重新核对。");
        var source=mediaPath(sha);
        if (!File.Exists(source)) throw new InvalidDataException("卡片原声文件缺失，请重新核对。");
        using var stream=File.OpenRead(source);
        if (stream.Length is <128 or >600000) throw new InvalidDataException("原声文件大小不符，请重新核对。");
        var bytes=new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        if (StudyCloud.Hash(bytes)!=sha) throw new InvalidDataException("原声文件摘要不符，请重新核对。");
        return new(Key(card),$"llplayer-{sha}.mp3",bytes,Encode(card.Expression),Encode(card.Meaning),
            Encode(example.English),Encode(example.Source));
    }

    internal static AnkiExportResult Export(CloudState state, Func<string,string> mediaPath,
        IEnumerable<(Guid CardId, Guid ExampleId)> selection, string parent)
    {
        var ids=selection.Distinct().ToArray();
        if (ids.Length is 0 or > MaxCards) throw new InvalidDataException($"请选中 1–{MaxCards} 张已核对的原声卡。");
        if (ids.Select(id=>id.CardId).Distinct().Count()!=ids.Length)
            throw new InvalidDataException("同一义项只选一段原声，请取消重复选择。");
        var available=Choices(state).ToDictionary(c=>(c.Card.CardId,c.Example.ExampleId));
        var media=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        var rows=new List<string>{"#separator:Tab","#html:true","#notetype:"+NoteType,
            "#columns:ID\t音频\t表达\t本句义\t英文原句\t出处"};
        foreach (var id in ids)
        {
            if (!available.TryGetValue(id,out var choice))
                throw new InvalidDataException("所选原声未核对、已撤回、包含路径或不再可导出，请重新选择。");
            var note=BuildNote(choice,mediaPath);
            media.TryAdd(note.MediaName,note.Mp3);
            rows.Add(string.Join('\t',note.Key,$"[sound:{note.MediaName}]",note.Expression,note.Meaning,note.English,note.Source));
        }
        var directory=Path.Combine(Path.GetFullPath(parent),$"llplayer-anki-{Guid.NewGuid():N}");
        var mediaDir=Path.Combine(directory,"media"); Directory.CreateDirectory(mediaDir);
        foreach (var (name,bytes) in media)
        {
            using var file=new FileStream(Path.Combine(mediaDir,name),FileMode.CreateNew,FileAccess.Write);
            file.Write(bytes);
        }
        var guide=Path.Combine(directory,"Anki导入说明.txt"); NewText(guide,$"""
            1. 首次在桌面 Anki 创建一个从“基本 (Basic)”复制的独立笔记类型：{NoteType}。
               六个字段按此顺序排列：ID、音频、表达、本句义、英文原句、出处。
               ID 必须是第一个字段；只保留一张卡片模板，不修改已有牌组的笔记类型。
               正面模板：{Front}
               背面模板：{Back}
            2. 把本目录 media 文件夹里的 MP3 复制到当前 Anki 配置的 collection.media 目录（不复制外层文件夹）。
               Windows 默认在 %APPDATA%\Anki2\<配置名>\collection.media；自定义安装以实际配置目录为准。
            3. 导入 cards.tsv：选同一个笔记类型，六列按顺序映射，允许 HTML。
               已有笔记选“更新”，匹配范围选“笔记类型”，不要选“导入为新笔记”。
               ID 相同的笔记会更新，不按单词拼写去重。纠正同一义项时，请在 LLPlayer 勾选保留原卡 ID，再重新导出同一段。新音频使用新文件名。
            4. 先在桌面检查正面播放与翻面内容。确认有权上传后再手动同步，并等媒体同步完成。
               此备用导出不写入 Anki、不触发同步、不迁移成绩。撤回的义项请在 Anki 浏览器按 ID 搜索后手动暂停。
            """);
        var tsv=Path.Combine(directory,"cards.tsv"); NewText(tsv,string.Join('\n',rows)+"\n");
        return new(tsv,guide,ids.Length,media.Count);
    }

    internal static bool HasPath(string text) => Regex.IsMatch(text,
        @"[a-z]:[\\/]|\\\\|file:|%[a-z][a-z0-9_]*%[\\/]|(?:^|[\s""'(])/(?:home|users|mnt|var|tmp)/",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static string Encode(string value) => WebUtility.HtmlEncode(new string(value.Select(c=>char.IsControl(c)?' ':c).ToArray()).Trim()).Replace("[","&#91;",StringComparison.Ordinal);
    private static void NewText(string path,string text) { using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write); using var writer=new StreamWriter(stream,new UTF8Encoding(false)); writer.Write(text); }
}
