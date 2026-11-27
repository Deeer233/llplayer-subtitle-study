using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using static Program;

// The package's real JSON client calls this handler instead of a socket. Only the mock's
// notes/media persist on its side, so the queue check can restart the actual product services.
internal sealed class FakeAnki(string path) : HttpMessageHandler
{
    internal const string CorrectProfile = "Synthetic Anki profile A";
    private const string Model = "LLPlayer 原声 v1", Deck = "LLPlayer 原声复习";
    private const string Front = "{{音频}}";
    private const string Back = "{{FrontSide}}<hr id=answer><b>{{表达}}</b><p>{{本句义}}</p><p>{{英文原句}}</p><p>{{出处}}</p>";
    private const long NoteId = 2001;
    private static readonly string[] Columns = ["ID", "音频", "表达", "本句义", "英文原句", "出处"];

    private readonly MockState _state = File.Exists(path) ? JsonSerializer.Deserialize<MockState>(File.ReadAllText(path))! : new([], []);
    internal string Profile = CorrectProfile;
    internal bool Offline, DropAddAcknowledgement;
    internal int Calls, Adds, Updates, Stores;
    internal int Mutations => Adds + Updates + Stores;
    internal int NoteCount => _state.Fields.Count == 0 ? 0 : 1;
    internal int MediaCount => _state.Media.Count;
    internal string StableKey => _state.Fields["ID"];
    private sealed record MockState(Dictionary<string,string> Fields, Dictionary<string,string> Media);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Guard(request.RequestUri == new Uri("http://127.0.0.1:8765/") && request.Method == HttpMethod.Post, "wrong mock Anki endpoint or HTTP verb");
        Calls++;
        if (Offline) throw new HttpRequestException("simulated Anki unavailable; no socket was opened");
        using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var root = doc.RootElement;
        Guard(root.GetProperty("version").GetInt32() == 6, "unexpected Anki protocol version");
        var action = root.GetProperty("action").GetString();
        root.TryGetProperty("params", out var args);
        if (action is "storeMediaFile" or "addNote" or "updateNoteFields")
            Guard(Profile == CorrectProfile, "mutation attempted against wrong synthetic profile");
        object? result = action switch
        {
            "getActiveProfile" => Profile,
            "modelNames" => new[] { Model },
            "findModelsByName" => new[] { new {
                name = Model, type = 0, flds = Columns.Select((name,ord) => new { name, ord }),
                tmpls = new[] { new { name = "Original", ord = 0, qfmt = Front, afmt = Back, did = 0 } },
            } },
            "findNotes" => NoteCount == 1 && args.GetProperty("query").GetString() == $"\"ID:{StableKey}\""
                ? new[] { NoteId } : Array.Empty<long>(),
            "notesInfo" => NoteInfo(args),
            "deckNamesAndIds" => new Dictionary<string,long> { [Deck] = 2100 },
            "retrieveMediaFile" => _state.Media.TryGetValue(args.GetProperty("filename").GetString()!, out var data) ? data : false,
            "storeMediaFile" => Store(args),
            "addNote" => Add(args.GetProperty("note")),
            "updateNoteFields" => Update(args.GetProperty("note")),
            _ => throw new CheckFailure("unexpected simulated Anki action: " + action),
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { result, error = (string?)null }), Encoding.UTF8, "application/json"),
        };
    }

    private object NoteInfo(JsonElement args)
    {
        Guard(args.GetProperty("notes").EnumerateArray().Select(n => n.GetInt64()).SequenceEqual([NoteId]) && NoteCount == 1,
            "notesInfo addressed the wrong mock note");
        return new[] { new { noteId = NoteId, modelName = Model,
            fields = Columns.Select((name, order) => (name, info:new { value = _state.Fields[name], order })).ToDictionary(p => p.name, p => p.info) } };
    }

    private string Store(JsonElement args)
    {
        string filename = args.GetProperty("filename").GetString()!;
        Guard(args.GetProperty("deleteExisting").ValueKind == JsonValueKind.False
            && _state.Media.TryAdd(filename, args.GetProperty("data").GetString()!), "duplicate or overwriting mock media mutation");
        Stores++;
        Persist();
        return filename;
    }

    private long Add(JsonElement note)
    {
        Guard(NoteCount == 0 && note.GetProperty("deckName").GetString() == Deck && note.GetProperty("modelName").GetString() == Model,
            "duplicate mock add or incorrect deck/model");
        foreach (var field in note.GetProperty("fields").EnumerateObject()) _state.Fields.Add(field.Name, field.Value.GetString()!);
        Guard(_state.Fields.Keys.SequenceEqual(Columns) && _state.Media.Keys.Any(name => _state.Fields["音频"] == $"[sound:{name}]"),
            "wrong fields/order or missing exact mock media");
        Adds++;
        Persist();
        if (DropAddAcknowledgement)
        {
            Offline = true;
            throw new HttpRequestException("simulated lost response after mock addNote commit");
        }
        return NoteId;
    }

    private object? Update(JsonElement note)
    {
        var fields = note.GetProperty("fields");
        Guard(note.GetProperty("id").GetInt64() == NoteId && fields.EnumerateObject().Select(f => f.Name).ToHashSet().SetEquals(["音频", "本句义"]),
            "correction did not patch exactly the changed mock fields");
        foreach (var field in fields.EnumerateObject()) _state.Fields[field.Name] = field.Value.GetString()!;
        Guard(_state.Media.Keys.Any(name => _state.Fields["音频"] == $"[sound:{name}]"), "corrected mock note lacks media");
        Updates++;
        Persist();
        return null;
    }

    private void Persist() => File.WriteAllText(path, JsonSerializer.Serialize(_state));
}
