using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LLPlayer.Services;

internal interface IAnkiConnectClient
{
    Task<string> GetProfileAsync(CancellationToken token);
    Task WriteAsync(string profile, AnkiNotePayload payload, CancellationToken token);
}

internal sealed class AnkiConnectException : Exception
{
    internal bool Unavailable { get; }

    internal AnkiConnectException(string message, bool unavailable = false) : base(message)
    {
        Unavailable = unavailable;
    }
}

internal sealed class AnkiConnectClient : IAnkiConnectClient
{
    private const string Deck = "LLPlayer 原声复习";
    private const string NoteType = AnkiExportService.NoteType;
    private const string ProfileUnavailable = "无法读取当前 Anki 配置。请检查 AnkiConnect 是否支持 getActiveProfile 并允许本机调用。";
    private const int MaxMediaBytes = 600_000;
    private const int MaxFieldChars = 16_384;
    private const int MaxResponseBytes = 1_048_576;
    private static readonly Uri Endpoint = new("http://127.0.0.1:8765/");
    private static readonly string[] FieldNames = ["ID", "音频", "表达", "本句义", "英文原句", "出处"];
    // Serializes this process's read-before-write operations, not writes from Anki or other clients.
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private static readonly HttpClient LocalHttp = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        MaxResponseHeadersLength = 16,
        MaxResponseDrainSize = 0
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http;

    internal AnkiConnectClient() => _http = LocalHttp;

    // The caller retains ownership of an injected handler.
    internal AnkiConnectClient(HttpMessageHandler handler) =>
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<string> GetProfileAsync(CancellationToken token)
    {
        var result = await CallAsync("getActiveProfile", null, token).ConfigureAwait(false);
        var profile = Text(result);
        if (result.ValueKind == JsonValueKind.Null || profile == "")
            throw new AnkiConnectException("桌面 Anki 没有打开可用的配置。", unavailable: true);
        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 256 || profile.Any(char.IsControl))
            throw new AnkiConnectException(ProfileUnavailable);
        return profile;
    }

    public async Task WriteAsync(string profile, AnkiNotePayload payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(profile)) throw new AnkiConnectException("尚未绑定 Anki 配置，已停止写入。");
        var (fields, mediaData) = Prepare(payload);
        await WriteGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await RequireProfileAsync(profile, token).ConfigureAwait(false);
            var template = await FindTemplateAsync(token).ConfigureAwait(false);
            var existing = await FindNoteAsync(payload.Key, token).ConfigureAwait(false);
            if (existing is not null && template is null) throw ModelMismatch();
            var hasMedia = await HasMediaAsync(payload.MediaName, mediaData, token).ConfigureAwait(false);
            if (hasMedia && existing is not null && Matches(existing, fields))
            {
                await RequireProfileAsync(profile, token).ConfigureAwait(false);
                return;
            }

            template ??= await CreateModelAsync(profile, token).ConfigureAwait(false);
            var deckId = existing is null
                ? await EnsureDeckAsync(profile, template.Value, token).ConfigureAwait(false)
                : (long?)null;
            if (!hasMedia)
                await StoreMediaAsync(profile, payload.MediaName, mediaData, token).ConfigureAwait(false);

            // Recheck after bootstrap/media writes; neither templates nor candidate identity are cached.
            var currentTemplate = await FindTemplateAsync(token).ConfigureAwait(false) ?? throw ModelMismatch();
            var current = await FindNoteAsync(payload.Key, token).ConfigureAwait(false);
            if (current is not null && Matches(current, fields))
            {
                await RequireProfileAsync(profile, token).ConfigureAwait(false);
                return;
            }
            if (existing?.Id != current?.Id)
                throw new AnkiConnectException("同 ID 的 Anki 笔记在准备写入时已变化，已停止。请检查后重试。");
            if (current is null) RequireTemplateDeck(currentTemplate, deckId);
            await WriteNoteAsync(profile, payload.Key, fields, current, token).ConfigureAwait(false);
        }
        finally { WriteGate.Release(); }
    }

    private static (Dictionary<string, string> Fields, string MediaData) Prepare(AnkiNotePayload payload)
    {
        if (payload is null || payload.Key is not { Length: 65 } ||
            !Regex.IsMatch(payload.Key, @"\A[0-9a-f]{32}:[0-9a-f]{32}\z", RegexOptions.CultureInvariant))
            throw new AnkiConnectException("原声卡的稳定 ID 无效，已停止写入。");
        if (payload.Mp3 is not { Length: >= 128 and <= MaxMediaBytes })
            throw new AnkiConnectException("原声音频大小不符，请重新核对。");
        if (new[] { payload.Expression, payload.Meaning, payload.English, payload.Source }
            .Any(value => value is null || value.Length > MaxFieldChars))
            throw new AnkiConnectException("原声卡字段无效或过长，已停止写入。");

        var mp3 = (byte[])payload.Mp3.Clone();
        var name = $"llplayer-{Convert.ToHexStringLower(SHA256.HashData(mp3))}.mp3";
        if (!string.Equals(payload.MediaName, name, StringComparison.Ordinal))
            throw new AnkiConnectException("原声音频文件名与内容摘要不符，请重新核对。");
        return (new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ID"] = payload.Key,
            ["音频"] = $"[sound:{name}]",
            ["表达"] = payload.Expression,
            ["本句义"] = payload.Meaning,
            ["英文原句"] = payload.English,
            ["出处"] = payload.Source
        }, Convert.ToBase64String(mp3));
    }

    private async Task RequireProfileAsync(string expected, CancellationToken token)
    {
        if (!string.Equals(await GetProfileAsync(token).ConfigureAwait(false), expected, StringComparison.Ordinal))
            throw new AnkiConnectException("当前 Anki 配置与已绑定配置不一致，已停止。请切回已绑定的配置。");
    }

    private async Task<JsonElement?> FindTemplateAsync(CancellationToken token)
    {
        var names = await CallAsync("modelNames", null, token).ConfigureAwait(false);
        if (names.ValueKind != JsonValueKind.Array) throw InvalidReply();
        bool found = false;
        foreach (var entry in names.EnumerateArray())
        {
            var name = Text(entry) ?? throw InvalidReply();
            if (!string.Equals(name, NoteType, StringComparison.OrdinalIgnoreCase)) continue;
            if (found || name != NoteType) throw ModelMismatch();
            found = true;
        }
        if (!found) return null;

        // Read the actual model, including any per-template deck override. Template names and CSS can differ.
        var models = await CallAsync("findModelsByName", new { modelNames = new[] { NoteType } }, token)
            .ConfigureAwait(false);
        if (!IsArrayOfSize(models, 1)) throw ModelMismatch();
        var model = models[0];
        var fields = Property(model, "flds");
        var templates = Property(model, "tmpls");
        if (Text(Property(model, "name")) != NoteType || Integer(Property(model, "type")) != 0 ||
            !IsArrayOfSize(fields, FieldNames.Length) || !IsArrayOfSize(templates, 1))
            throw ModelMismatch();
        for (int i = 0; i < FieldNames.Length; i++)
        {
            if (Text(Property(fields[i], "name")) != FieldNames[i] || Integer(Property(fields[i], "ord")) != i)
                throw ModelMismatch();
        }
        var template = templates[0];
        if (Integer(Property(template, "ord")) != 0 || Text(Property(template, "qfmt")) != AnkiExportService.Front ||
            Text(Property(template, "afmt")) != AnkiExportService.Back)
            throw ModelMismatch();
        return template;
    }

    private async Task<JsonElement> CreateModelAsync(string profile, CancellationToken token)
    {
        var (_, error) = await MutateAsync(profile, "createModel", new
        {
            modelName = NoteType,
            inOrderFields = FieldNames,
            isCloze = false,
            cardTemplates = new[] { new { Name = "原声", Front = AnkiExportService.Front, Back = AnkiExportService.Back } }
        }, token).ConfigureAwait(false);
        return await FindTemplateAsync(token).ConfigureAwait(false) ?? throw (error ?? ModelMismatch());
    }

    private async Task<long?> FindDeckAsync(CancellationToken token)
    {
        var decks = await CallAsync("deckNamesAndIds", null, token).ConfigureAwait(false);
        if (decks.ValueKind != JsonValueKind.Object) throw InvalidReply();
        long? found = null;
        foreach (var entry in decks.EnumerateObject())
        {
            var name = SafeString(() => entry.Name);
            if (!string.Equals(name, Deck, StringComparison.OrdinalIgnoreCase)) continue;
            if (name != Deck || found is not null)
                throw new AnkiConnectException("Anki 中存在名称冲突的原声复习牌组，已停止写入。");
            found = PositiveId(entry.Value) ?? throw InvalidReply();
        }
        return found;
    }

    private async Task<long> EnsureDeckAsync(string profile, JsonElement template, CancellationToken token)
    {
        var id = await FindDeckAsync(token).ConfigureAwait(false);
        RequireTemplateDeck(template, id);
        if (id is long existing) return existing;
        var (_, error) = await MutateAsync(profile, "createDeck", new { deck = Deck }, token).ConfigureAwait(false);
        return await FindDeckAsync(token).ConfigureAwait(false) ??
            throw (error ?? new AnkiConnectException("未能确认原声复习牌组已创建。"));
    }

    private static void RequireTemplateDeck(JsonElement template, long? deckId)
    {
        var value = Property(template, "did");
        var id = Integer(value);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || id == 0 ||
            (id is > 0 && id == deckId)) return;
        throw new AnkiConnectException("原声笔记模板指定了其他牌组，未新增笔记；不会修改现有模板。");
    }

    private sealed record Note(long Id, Dictionary<string, string> Fields);

    private async Task<Note?> FindNoteAsync(string key, CancellationToken token)
    {
        // The key grammar is fixed in Prepare. Search across decks to include notes imported from TSV.
        var ids = await CallAsync("findNotes", new { query = $"\"ID:{key}\"" }, token).ConfigureAwait(false);
        if (ids.ValueKind != JsonValueKind.Array) throw InvalidReply();
        if (ids.GetArrayLength() == 0) return null;
        if (ids.GetArrayLength() != 1)
            throw new AnkiConnectException("Anki 中有多个相同 ID 的笔记，未写入。请先解决重复笔记。");
        var id = PositiveId(ids[0]) ?? throw InvalidReply();
        var result = await CallAsync("notesInfo", new { notes = new[] { id } }, token).ConfigureAwait(false);
        if (!IsArrayOfSize(result, 1)) throw NoteMismatch();
        var note = result[0];
        var fields = Property(note, "fields");
        if (PositiveId(Property(note, "noteId")) != id || Text(Property(note, "modelName")) != NoteType ||
            fields.ValueKind != JsonValueKind.Object || fields.GetPropertyCount() != FieldNames.Length)
            throw NoteMismatch();
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < FieldNames.Length; i++)
        {
            var field = Property(fields, FieldNames[i]);
            if (Integer(Property(field, "order")) != i || Text(Property(field, "value")) is not string value)
                throw NoteMismatch();
            contents.Add(FieldNames[i], value);
        }
        if (contents["ID"] != key) throw NoteMismatch();
        return new Note(id, contents);
    }

    private static bool Matches(Note note, Dictionary<string, string> fields) =>
        fields.All(field => note.Fields[field.Key] == field.Value);

    private async Task<bool> HasMediaAsync(string filename, string mediaData, CancellationToken token)
    {
        var result = await CallAsync("retrieveMediaFile", new { filename }, token).ConfigureAwait(false);
        if (result.ValueKind == JsonValueKind.False) return false;
        var stored = Text(result) ?? throw InvalidReply();
        // AnkiConnect returns canonical base64, so equality also checks the exact audio bytes.
        if (!string.Equals(stored, mediaData, StringComparison.Ordinal))
            throw new AnkiConnectException("Anki 中的同名原声音频内容不同，不会覆盖；请先检查媒体文件。");
        return true;
    }

    private async Task StoreMediaAsync(string profile, string filename, string mediaData, CancellationToken token)
    {
        var (result, error) = await MutateAsync(profile, "storeMediaFile",
            new { filename, data = mediaData, deleteExisting = false }, token).ConfigureAwait(false);
        if (Text(result) is string storedName && !string.Equals(storedName, filename, StringComparison.Ordinal))
            throw new AnkiConnectException("Anki 为原声音频返回了其他文件名，已停止；不会引用改名的文件。");
        if (!await HasMediaAsync(filename, mediaData, token).ConfigureAwait(false))
            throw error ?? new AnkiConnectException("未能确认原声音频已写入 Anki。");
    }

    private async Task WriteNoteAsync(string profile, string key, Dictionary<string, string> fields, Note? current,
        CancellationToken token)
    {
        long? expectedId = current?.Id;
        AnkiConnectException? error;
        if (current is null)
        {
            var attempt = await MutateAsync(profile, "addNote", new
            {
                note = new { deckName = Deck, modelName = NoteType, fields, options = new { allowDuplicate = false } }
            }, token).ConfigureAwait(false);
            expectedId = PositiveId(attempt.Result);
            error = attempt.Error ?? (expectedId is null ? InvalidReply() : null);
        }
        else
        {
            var changes = fields.Where(field => field.Key != "ID" && current.Fields[field.Key] != field.Value)
                .ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
            var attempt = await MutateAsync(profile, "updateNoteFields", new { note = new { id = current.Id, fields = changes } }, token)
                .ConfigureAwait(false);
            error = attempt.Error ?? (attempt.Result.ValueKind != JsonValueKind.Null ? InvalidReply() : null);
        }

        // Includes rejected/ambiguous acknowledgements. Never blindly resend a mutation.
        var saved = await FindNoteAsync(key, token).ConfigureAwait(false);
        await RequireProfileAsync(profile, token).ConfigureAwait(false);
        if (saved is null || (expectedId is long id && saved.Id != id) || !Matches(saved, fields))
            throw error ?? new AnkiConnectException("未能确认原声笔记已保存。下次写入会先重查稳定 ID。");
    }

    private async Task<(JsonElement Result, AnkiConnectException? Error)> MutateAsync(string profile, string action,
        object parameters, CancellationToken token)
    {
        // AnkiConnect does not have an atomic profile-check-and-write action.
        await RequireProfileAsync(profile, token).ConfigureAwait(false);
        JsonElement result = default;
        AnkiConnectException? error = null;
        try { result = await CallAsync(action, parameters, token).ConfigureAwait(false); }
        catch (AnkiConnectException exception) { error = exception; }
        await RequireProfileAsync(profile, token).ConfigureAwait(false);
        return (result, error);
    }

    private async Task<JsonElement> CallAsync(string action, object? parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8)); // Headers and the entire response body.
        // Send a known Content-Length for older AnkiConnect HTTP parsers. Keep Name/Front/Back case intact.
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
            new { action, version = 6, @params = parameters ?? new { } }));
        content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content };
        request.Headers.ConnectionClose = true;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new AnkiConnectException("本机端点未返回有效的 AnkiConnect HTTP 响应。");
            if (response.Content.Headers.ContentLength is > MaxResponseBytes) throw InvalidReply();
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var body = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var count = Math.Min(chunk.Length, MaxResponseBytes + 1 - (int)body.Length);
                int read = await stream.ReadAsync(chunk.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (body.Length + read > MaxResponseBytes) throw InvalidReply();
                body.Write(chunk, 0, read);
            }
            deadline.Token.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length), new JsonDocumentOptions
            {
                MaxDepth = 24, AllowDuplicateProperties = false
            });
            var root = doc.RootElement;
            var result = Property(root, "result");
            var error = Property(root, "error");
            if (root.ValueKind != JsonValueKind.Object || root.GetPropertyCount() != 2 ||
                result.ValueKind == JsonValueKind.Undefined || error.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                throw InvalidReply();
            if (error.ValueKind == JsonValueKind.String)
            {
                if (result.ValueKind != JsonValueKind.Null) throw InvalidReply();
                throw Rejected(action, Text(error));
            }
            return result.Clone();
        }
        catch (JsonException) { throw InvalidReply(); }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            token.ThrowIfCancellationRequested();
            throw new AnkiConnectException("无法连接桌面 Anki，或 AnkiConnect 响应超时、连接中断。", unavailable: true);
        }
    }

    private static JsonElement Property(JsonElement value, string name)
    {
        try { return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) ? p : default; }
        catch (InvalidOperationException) { throw InvalidReply(); }
    }

    private static string? Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? SafeString(value.GetString) : null;

    private static string? SafeString(Func<string?> read)
    {
        // JsonDocument can defer validation of a string's UTF-8/escapes until it is decoded.
        try { return read(); }
        catch (InvalidOperationException) { throw InvalidReply(); }
    }
    private static long? Integer(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    private static long? PositiveId(JsonElement value) => Integer(value) is long id && id > 0 ? id : null;
    private static bool IsArrayOfSize(JsonElement value, int count) =>
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == count;

    private static AnkiConnectException InvalidReply() => new("AnkiConnect 响应无效或过大，已停止写入。");
    private static AnkiConnectException ModelMismatch() =>
        new("Anki 原声笔记类型的字段或模板不匹配，已停止。请按 TSV 导入说明核对；不会修改现有模板。");
    private static AnkiConnectException NoteMismatch() =>
        new("找到的 Anki 笔记 ID、笔记类型或字段不匹配，已停止，未修改该笔记。");

    private static AnkiConnectException Rejected(string action, string? error)
    {
        // Only recognize a known availability signal; never expose server-provided error text.
        if (error == "collection is not available")
            return new AnkiConnectException("桌面 Anki 当前没有打开可用的词库。", unavailable: true);
        return new AnkiConnectException(action switch
        {
            "getActiveProfile" => ProfileUnavailable,
            "retrieveMediaFile" or "storeMediaFile" => "AnkiConnect 无法读取或保存原声音频；不会尝试覆盖。",
            "createModel" => "AnkiConnect 拒绝创建原声笔记类型。请检查桌面 Anki。",
            "createDeck" => "AnkiConnect 拒绝创建原声复习牌组。请检查桌面 Anki。",
            "addNote" => "AnkiConnect 拒绝新增原声笔记。请检查重复 ID 或目标牌组。",
            "updateNoteFields" => "AnkiConnect 拒绝更新原声笔记的字段。请检查桌面 Anki。",
            _ => "AnkiConnect 拒绝读取原声复习数据。请检查桌面 Anki 和插件版本。"
        });
    }
}
