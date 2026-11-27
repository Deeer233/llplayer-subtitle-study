using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LLPlayer.Services;

internal static class AnkiConnectChecks
{
    private const string Profile = "原声测试 A";
    private const string OtherProfile = "原声测试 B";
    private const string Deck = "LLPlayer 原声复习";
    private const string ImportedDeck = "已有 TSV 牌组";
    // This is the previously published TSV schema, independent of the writer's constants.
    private const string NoteType = "LLPlayer 原声 v1";
    private const string Front = "{{音频}}";
    private const string Back = "{{FrontSide}}<hr id=answer><b>{{表达}}</b><p>{{本句义}}</p><p>{{英文原句}}</p><p>{{出处}}</p>";
    private const string Key = "0123456789abcdef0123456789abcdef:00112233445566778899aabbccddeeff";
    private const long DeckId = 1_727_000_000_101L;
    private const long ModelId = 1_727_000_000_102L;
    private const long NoteId = 1_727_000_000_103L;
    private const long CardId = 1_727_000_000_104L;
    private const string PrivateError = @"C:\Private\fixture-secret.mp3";
    private static readonly string[] Columns = ["ID", "音频", "表达", "本句义", "英文原句", "出处"];

    internal static async Task RunAsync()
    {
        await BootstrapsAndAddsAsync();
        await ReconcilesLostAcknowledgementsAsync();
        await RetriesAfterDisconnectWithoutDuplicatingAsync();
        await CorrectsTsvNoteWithoutChangingCardAsync();
        await RejectsUnsafeNotesAndModelsAsync();
        await RejectsMediaConflictsAsync();
        await StopsOnProfileChangesAsync();
        await RejectsInvalidInputsAndRepliesAsync();
        await HonorsCancellationAndTimeoutAsync();
    }

    private static async Task BootstrapsAndAddsAsync()
    {
        using var anki = new FakeAnki();
        var payload = Payload();
        var client = anki.Client();
        Check(await client.GetProfileAsync(default) == Profile, "getActiveProfile must return the exact bound name");
        await client.WriteAsync(Profile, payload, default);
        await anki.Client().WriteAsync(Profile, payload, default);

        CheckFirstWrite(anki, payload);
        Check(anki.Count("findModelsByName") > 0 && anki.Count("notesInfo") > 0, "write did not verify the model and the note");
    }

    private static async Task ReconcilesLostAcknowledgementsAsync()
    {
        using var anki = new FakeAnki();
        var payload = Payload();
        anki.DropReplies.UnionWith(["createModel", "createDeck", "storeMediaFile", "addNote"]);
        await anki.Client().WriteAsync(Profile, payload, default);
        Check(anki.DropReplies.Count == 0, "lost-response fixture did not exercise all four commits");
        CheckFirstWrite(anki, payload);
    }

    private static async Task RetriesAfterDisconnectWithoutDuplicatingAsync()
    {
        using var anki = new FakeAnki();
        var payload = Payload();
        anki.DropReplies.Add("addNote");
        anki.AfterAction = action => { if (action == "addNote") anki.Offline = true; };
        var client = anki.Client();
        await RejectsAsync(() => client.WriteAsync(Profile, payload, default), unavailable: true);
        Check(anki.Notes.Count == 1 && anki.DropReplies.Count == 0, "disconnect must occur after Anki committed the note");

        anki.Offline = false;
        anki.AfterAction = null;
        var retryStart = anki.Calls.Count;
        await anki.Client().WriteAsync(Profile, payload, default);
        Check(anki.Calls.Skip(retryStart).Select(call => call.Action).Contains("notesInfo"), "retry did not re-read the committed stable ID");
        CheckFirstWrite(anki, payload);
    }

    private static async Task CorrectsTsvNoteWithoutChangingCardAsync()
    {
        using var anki = new FakeAnki();
        var original = Payload();
        var revised = Payload(sample: 43, meaning: "更正本句义");
        var imported = anki.Import(original);
        var previousCard = imported.Card;
        var previousModel = anki.Model;
        anki.Media.Add(original.MediaName, original.Mp3);
        anki.DropReplies.Add("updateNoteFields");
        var client = anki.Client();
        await client.WriteAsync(Profile, revised, default);
        await client.WriteAsync(Profile, revised, default);

        var note = anki.Notes.Single();
        Check(note.Id == imported.Id && note.Card == previousCard && note.Card.Deck == ImportedDeck,
            "TSV correction replaced a note/card, moved its deck or altered its scheduling fixture");
        Check(anki.Model == previousModel && !anki.Decks.ContainsKey(Deck), "TSV correction modified the model or created an unnecessary deck");
        Check(anki.Count("storeMediaFile") == 1 && anki.Count("updateNoteFields") == 1 && anki.Mutations.Count() == 2,
            "correction must consist of one new media file and one fields-only update, even after a lost response");
        Check(anki.LastFieldUpdate is { Count: 2 } patch && patch.ContainsKey("音频") && patch.ContainsKey("本句义"),
            "correction touched the stable ID or unrelated fields");
        CheckFields(note, revised);
        Check(anki.Media[original.MediaName].SequenceEqual(original.Mp3) && anki.Media[revised.MediaName].SequenceEqual(revised.Mp3),
            "correction overwrote old audio instead of using a new digest name");
    }

    private static async Task RejectsUnsafeNotesAndModelsAsync()
    {
        var payload = Payload();
        var cases = new (string Name, Action<FakeAnki> Seed)[]
        {
            ("changed template", a => a.Model = TsvModel() with { Answer = "{{表达}}" }),
            ("changed field order", a => a.Model = TsvModel() with { Fields = Columns.Reverse().ToArray() }),
            ("template deck override", a => a.Model = TsvModel() with { DeckOverride = 1 }),
            ("same ID in another model", a => a.Import(payload, modelName: "另一笔记类型")),
            ("ambiguous stable ID", a => { a.Import(payload); a.Import(payload, id: NoteId + 10); }),
            ("notesInfo missing an expected field", a => a.Import(payload).Fields.Remove("出处"))
        };
        foreach (var (name, seed) in cases)
        {
            using var anki = new FakeAnki();
            seed(anki);
            var client = anki.Client();
            await RejectsAsync(() => client.WriteAsync(Profile, payload, default));
            Check(!anki.Mutations.Any(), $"{name}: client modified Anki before validating the target");
        }

        using var deletedNote = new FakeAnki();
        var note = deletedNote.Import(payload);
        deletedNote.AfterAction = action => { if (action == "findNotes") deletedNote.Notes.Remove(note); };
        await RejectsAsync(() => deletedNote.Client().WriteAsync(Profile, payload, default));
        Check(deletedNote.Count("notesInfo") == 1 && !deletedNote.Mutations.Any(),
            "a note deleted after the stable ID search did not stop the write");
    }

    private static async Task RejectsMediaConflictsAsync()
    {
        var payload = Payload();
        var conflictingBytes = Payload(sample: 44).Mp3;
        using (var anki = new FakeAnki())
        {
            anki.Media.Add(payload.MediaName, conflictingBytes);
            await RejectsAsync(() => anki.Client().WriteAsync(Profile, payload, default));
            Check(!anki.Mutations.Any() && anki.Media[payload.MediaName].SequenceEqual(conflictingBytes),
                "existing same-name media was not rejected before any write");
        }
        using (var anki = new FakeAnki())
        {
            // Another client adds a conflicting name between retrieveMediaFile and storeMediaFile.
            anki.AfterAction = action => { if (action == "retrieveMediaFile") anki.Media.TryAdd(payload.MediaName, conflictingBytes); };
            await RejectsAsync(() => anki.Client().WriteAsync(Profile, payload, default));
            Check(anki.Count("storeMediaFile") == 1 && anki.Count("addNote") == 0 && anki.Notes.Count == 0,
                "a renamed media result was accepted for a note pointing at the original filename");
            Check(anki.Media.Count == 2 && anki.Media[payload.MediaName].SequenceEqual(conflictingBytes),
                "deleteExisting:false did not preserve the concurrently installed media");
        }
    }

    private static async Task StopsOnProfileChangesAsync()
    {
        var payload = Payload();
        using (var anki = new FakeAnki { ActiveProfile = OtherProfile })
        {
            await RejectsAsync(() => anki.Client().WriteAsync(Profile, payload, default));
            Check(!anki.Mutations.Any(), "the client wrote to a different active profile");
        }
        using (var anki = new FakeAnki())
        {
            anki.AfterAction = action => { if (action == "retrieveMediaFile") anki.ActiveProfile = OtherProfile; };
            await RejectsAsync(() => anki.Client().WriteAsync(Profile, payload, default));
            Check(!anki.Mutations.Any(), "switching profiles after preflight did not block the first mutation");
        }
        using (var anki = new FakeAnki())
        {
            anki.AfterAction = action => { if (action == "storeMediaFile") anki.ActiveProfile = OtherProfile; };
            await RejectsAsync(() => anki.Client().WriteAsync(Profile, payload, default));
            Check(anki.Count("storeMediaFile") == 1 && anki.Count("addNote") == 0 && anki.Mutations.All(call => call.Profile == Profile),
                "a profile switch after media commit was not caught before note creation");
        }
    }

    private static async Task RejectsInvalidInputsAndRepliesAsync()
    {
        using (var anki = new FakeAnki())
        {
            var payload = Payload();
            var client = anki.Client();
            await RejectsAsync(() => client.WriteAsync(Profile, payload with { MediaName = "wrong.mp3" }, default));
            await RejectsAsync(() => client.WriteAsync(Profile, payload with { Key = "not a stable ID" }, default));
            await RejectsAsync(() => client.WriteAsync("", payload, default));
            Check(anki.Calls.Count == 0, "invalid local input reached the endpoint");
        }
        using (var anki = new FakeAnki { ActiveProfile = null })
            await RejectsAsync(() => anki.Client().GetProfileAsync(default), unavailable: true);

        foreach (var body in new[] { "not JSON", """{"result":42,"error":null}""",
            """{"result":"A","result":"B","error":null}""", """{"result":"\uD800","error":null}""",
            """{"result":null,"error":"\uD800"}""", new string('x', 1_048_577) })
        {
            using var handler = new ResponseHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
            await RejectsAsync(() => new AnkiConnectClient(handler).GetProfileAsync(default));
        }
        using (var handler = new ResponseHandler(_ => Task.FromResult(JsonReply(null, PrivateError))))
            await RejectsAsync(() => new AnkiConnectClient(handler).GetProfileAsync(default));
        using (var handler = new ResponseHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            { Headers = { Location = new Uri("https://fixture.invalid/") } })))
            await RejectsAsync(() => new AnkiConnectClient(handler).GetProfileAsync(default));
    }

    private static async Task HonorsCancellationAndTimeoutAsync()
    {
        using var canceled = new CancellationTokenSource();
        using (var handler = new ResponseHandler(token =>
        {
            canceled.Cancel(); // While SendAsync is in flight, not before the request.
            return Task.FromCanceled<HttpResponseMessage>(token);
        }))
        {
            try
            {
                await new AnkiConnectClient(handler).GetProfileAsync(canceled.Token);
                throw new InvalidOperationException("Expected caller cancellation");
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == canceled.Token) { }
        }

        // Bound the test even if the production request deadline is accidentally removed.
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var stalled = new ResponseHandler(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Expected the request deadline to cancel the transport");
        });
        await RejectsAsync(() => new AnkiConnectClient(stalled).GetProfileAsync(watchdog.Token)
            .WaitAsync(watchdog.Token), unavailable: true);
    }

    private static void CheckFirstWrite(FakeAnki anki, AnkiNotePayload payload)
    {
        Check(anki.Count("createModel") == 1 && anki.Count("createDeck") == 1 && anki.Count("storeMediaFile") == 1 &&
            anki.Count("addNote") == 1 && anki.Mutations.Count() == 4, "bootstrap or retry sent extra mutations");
        var note = anki.Notes.Single();
        Check(note.Id == NoteId && note.Card.Deck == Deck && note.ModelName == NoteType, "new note did not use the dedicated deck and model");
        Check(anki.Media.Count == 1 && anki.Media[payload.MediaName].SequenceEqual(payload.Mp3), "new audio did not round-trip under its digest name");
        CheckFields(note, payload);
    }

    private static void CheckFields(StoredNote note, AnkiNotePayload payload)
    {
        var expected = FieldsOf(payload);
        Check(note.Fields.Count == expected.Count && expected.All(field => note.Fields.TryGetValue(field.Key, out var value) && value == field.Value),
            "note fields did not round-trip intact (including HTML-encoded values and the sound reference)");
    }

    private static AnkiNotePayload Payload(byte sample = 42, string meaning = "旧本句义")
    {
        var bytes = Enumerable.Repeat(sample, 128).ToArray(); // Synthetic media; playback is not under test.
        return new(Key, MediaName(bytes), bytes, "&lt;term&gt;", meaning, "A &amp; B.", "测试集 1");
    }

    private static string MediaName(byte[] bytes) => $"llplayer-{Convert.ToHexStringLower(SHA256.HashData(bytes))}.mp3";

    private static Dictionary<string, string> FieldsOf(AnkiNotePayload payload) => new(StringComparer.Ordinal)
    {
        ["ID"] = payload.Key, ["音频"] = $"[sound:{payload.MediaName}]", ["表达"] = payload.Expression,
        ["本句义"] = payload.Meaning, ["英文原句"] = payload.English, ["出处"] = payload.Source
    };

    private static NoteModel TsvModel() => new(Columns.ToArray(), "Card 1", Front, Back);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"AnkiConnect regression: {message}.");
    }

    private static async Task RejectsAsync(Func<Task> work, bool unavailable = false)
    {
        try { await work(); }
        catch (AnkiConnectException ex)
        {
            Check(ex.Unavailable == unavailable, $"unexpected availability classification: {ex.Message}");
            Check(ex.InnerException is null && !ex.Message.Contains("fixture-secret", StringComparison.Ordinal), "raw server/transport error leaked");
            return;
        }
        throw new InvalidOperationException("AnkiConnect regression: expected a safe AnkiConnectException.");
    }

    private static void Keys(JsonElement obj, params string[] names) =>
        Check(obj.ValueKind == JsonValueKind.Object && obj.GetPropertyCount() == names.Length && names.All(name => obj.TryGetProperty(name, out _)),
            $"wrong JSON keys; expected {string.Join(", ", names)}");

    private static string Text(JsonElement obj, string name) => obj.GetProperty(name).GetString() ??
        throw new InvalidOperationException($"AnkiConnect regression: expected string at {name}.");

    private static HttpResponseMessage JsonReply(object? result, string? error = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { result, error }), Encoding.UTF8, "application/json")
    };

    private sealed class ResponseHandler(Func<CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(token);
    }

    private sealed record ReviewCard(long Id, string Deck, int Queue, int Due, int Interval, int Reps);
    private sealed record StoredNote(long Id, string ModelName, Dictionary<string, string> Fields, ReviewCard Card);
    private sealed record NoteModel(string[] Fields, string TemplateName, string Question, string Answer, long? DeckOverride = null)
    {
        internal object Wire() => new
        {
            id = ModelId, name = NoteType, type = 0, sortf = 0, css = ".card { text-align: center; }",
            flds = Fields.Select((name, ord) => new { name, ord }).ToArray(),
            tmpls = new[] { new { name = TemplateName, ord = 0, qfmt = Question, afmt = Answer, did = DeckOverride } }
        };
    }

    // Deliberately only the v6 subset used by this writer, not a general Anki simulator.
    // Public reference: FooSoft/anki-connect @ 1f872f9ea7149dc29b362971b4e22dc67a22e41a
    // plugin/__init__.py (action signatures), plugin/legacy.py (parameters/results), plugin/web.py (v6 envelope).
    private sealed class FakeAnki : HttpMessageHandler
    {
        internal string? ActiveProfile { get; set; } = Profile;
        internal NoteModel? Model { get; set; }
        internal bool Offline { get; set; }
        internal Action<string>? AfterAction { get; set; }
        internal HashSet<string> DropReplies { get; } = new(StringComparer.Ordinal);
        internal List<(string Action, string? Profile)> Calls { get; } = [];
        internal Dictionary<string, long> Decks { get; } = new(StringComparer.Ordinal) { ["Default"] = 1 };
        internal Dictionary<string, byte[]> Media { get; } = new(StringComparer.Ordinal);
        internal List<StoredNote> Notes { get; } = [];
        internal Dictionary<string, string>? LastFieldUpdate { get; private set; }
        internal IEnumerable<(string Action, string? Profile)> Mutations => Calls.Where(call => call.Action is
            "createModel" or "createDeck" or "storeMediaFile" or "addNote" or "updateNoteFields");

        internal IAnkiConnectClient Client() => new AnkiConnectClient(this);
        internal int Count(string action) => Calls.Count(call => call.Action == action);

        internal StoredNote Import(AnkiNotePayload payload, long id = NoteId, string modelName = NoteType)
        {
            Model ??= TsvModel();
            Decks.TryAdd(ImportedDeck, DeckId + 10);
            var note = new StoredNote(id, modelName, FieldsOf(payload), new(CardId + (id - NoteId), ImportedDeck, 2, 110, 42, 7));
            Notes.Add(note);
            return note;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == "http://127.0.0.1:8765/", "not a POST to the fixed IPv4 loopback URI");
            var content = request.Content ?? throw new InvalidOperationException("Expected a JSON request body.");
            Check(content.Headers.ContentType?.MediaType == "application/json" && content.Headers.ContentLength is > 0 &&
                !request.Headers.Contains("Cookie"), "expected length-framed JSON without cookies");
            using var doc = JsonDocument.Parse(await content.ReadAsByteArrayAsync(token));
            var root = doc.RootElement;
            Keys(root, "action", "version", "params");
            Check(root.GetProperty("version").GetInt32() == 6, "request did not select AnkiConnect v6");
            var action = Text(root, "action");
            Calls.Add((action, ActiveProfile));
            if (Offline) throw new HttpRequestException(PrivateError);

            var (result, error) = Dispatch(action, root.GetProperty("params"));
            AfterAction?.Invoke(action);
            if (DropReplies.Remove(action)) throw new HttpRequestException(PrivateError);
            return JsonReply(result, error);
        }

        private (object? Result, string? Error) Dispatch(string action, JsonElement args)
        {
            switch (action)
            {
                case "getActiveProfile": Keys(args); return (ActiveProfile, null);
                case "modelNames": Keys(args); return (Model is null ? Array.Empty<string>() : new[] { NoteType }, null);
                case "findModelsByName":
                    Keys(args, "modelNames");
                    Check(args.GetProperty("modelNames").EnumerateArray().Select(name => name.GetString()).SequenceEqual(new[] { NoteType }),
                        "wrong note type lookup");
                    return Model is null ? (null, "model was not found") : (new[] { Model.Wire() }, null);
                case "createModel":
                    Keys(args, "modelName", "inOrderFields", "isCloze", "cardTemplates");
                    if (Model is not null) return (null, "Model name already exists");
                    Check(Text(args, "modelName") == NoteType && args.GetProperty("isCloze").ValueKind == JsonValueKind.False,
                        "wrong model name or cloze mode");
                    var fields = args.GetProperty("inOrderFields").EnumerateArray().Select(field => field.GetString()!).ToArray();
                    Check(fields.SequenceEqual(Columns), "wrong ordered TSV fields");
                    var templates = args.GetProperty("cardTemplates");
                    Check(templates.GetArrayLength() == 1, "expected exactly one card template");
                    Keys(templates[0], "Name", "Front", "Back");
                    Model = new(fields, Text(templates[0], "Name"), Text(templates[0], "Front"), Text(templates[0], "Back"));
                    Check(Model.Question == Front && Model.Answer == Back, "wrong TSV-compatible question/answer template");
                    return (Model.Wire(), null);
                case "deckNamesAndIds": Keys(args); return (Decks, null);
                case "createDeck":
                    Keys(args, "deck");
                    Check(Text(args, "deck") == Deck, "wrong bootstrap deck");
                    Decks.TryAdd(Deck, DeckId);
                    return (Decks[Deck], null);
                case "findNotes":
                    Keys(args, "query");
                    var query = Text(args, "query");
                    Check(query == $"\"ID:{Key}\"", "expected a stable ID search across decks, not a word search");
                    return (Notes.Where(note => query == $"\"ID:{note.Fields["ID"]}\"").Select(note => note.Id).ToArray(), null);
                case "notesInfo":
                    Keys(args, "notes");
                    var ids = args.GetProperty("notes").EnumerateArray().Select(id => id.GetInt64()).ToArray();
                    // v6 preserves request order and returns {} for each ID no longer present.
                    return (ids.Select(id =>
                    {
                        var note = Notes.Find(candidate => candidate.Id == id);
                        return note is null ? new { } : NoteInfo(note);
                    }).ToArray(), null);
                case "retrieveMediaFile":
                    Keys(args, "filename");
                    return (Media.TryGetValue(Text(args, "filename"), out var bytes) ? Convert.ToBase64String(bytes) : false, null);
                case "storeMediaFile": return (StoreMedia(args), null);
                case "addNote": return AddNote(args);
                case "updateNoteFields":
                    Keys(args, "note");
                    var update = args.GetProperty("note");
                    Keys(update, "id", "fields"); // No tags, attachments, deck or scheduling operations are allowed.
                    var current = Notes.Single(note => note.Id == update.GetProperty("id").GetInt64());
                    var patch = ReadFields(update);
                    Check(patch.Count > 0 && patch.All(field => field.Key != "ID" && current.Fields.ContainsKey(field.Key)),
                        "update sent the stable ID or an unknown field");
                    foreach (var (name, value) in patch) current.Fields[name] = value;
                    LastFieldUpdate = patch;
                    return (null, null);
                default: throw new InvalidOperationException($"AnkiConnect regression: unexpected action {action}.");
            }
        }

        private object NoteInfo(StoredNote note) => new
        {
            noteId = note.Id, profile = ActiveProfile, modelName = note.ModelName, mod = 1_727_000_001L,
            tags = Array.Empty<string>(), cards = new[] { note.Card.Id },
            fields = Columns.Select((name, order) => (name, order)).Where(field => note.Fields.ContainsKey(field.name))
                .ToDictionary(field => field.name, field => new { value = note.Fields[field.name], order = field.order })
        };

        private string StoreMedia(JsonElement args)
        {
            Keys(args, "filename", "data", "deleteExisting");
            Check(args.GetProperty("deleteExisting").ValueKind == JsonValueKind.False, "media overwrite protection was not requested");
            var filename = Text(args, "filename");
            var data = Convert.FromBase64String(Text(args, "data"));
            Check(filename == MediaName(data), "media filename was not the SHA-256 of the uploaded bytes");
            if (Media.TryAdd(filename, data) || Media[filename].SequenceEqual(data)) return filename;
            // The protected API may return a different basename; its exact suffix is not contractual.
            var renamed = filename[..^4] + "-fixture.mp3";
            Media.Add(renamed, data);
            return renamed;
        }

        private (object? Result, string? Error) AddNote(JsonElement args)
        {
            Keys(args, "note");
            var note = args.GetProperty("note");
            Keys(note, "deckName", "modelName", "fields", "options");
            Check(Model is not null && Decks.ContainsKey(Deck) && Text(note, "modelName") == NoteType && Text(note, "deckName") == Deck,
                "addNote did not target the prepared model and deck");
            var options = note.GetProperty("options");
            Keys(options, "allowDuplicate");
            Check(options.GetProperty("allowDuplicate").ValueKind == JsonValueKind.False, "addNote did not explicitly disallow duplicates");
            Keys(note.GetProperty("fields"), Columns);
            var fields = ReadFields(note);
            Check(fields["ID"] == Key && Media.Keys.Any(filename => fields["音频"] == $"[sound:{filename}]"),
                "addNote sent the wrong stable ID or referenced audio that was not stored");
            if (Notes.Any(existing => existing.ModelName == NoteType && existing.Fields["ID"] == fields["ID"]))
                return (null, "cannot create note because it is a duplicate");
            Notes.Add(new(NoteId, NoteType, fields, new(CardId, Deck, 0, 1, 0, 0)));
            return (NoteId, null);
        }

        private static Dictionary<string, string> ReadFields(JsonElement note) => note.GetProperty("fields").EnumerateObject()
            .ToDictionary(field => field.Name, field => Text(note.GetProperty("fields"), field.Name), StringComparer.Ordinal);
    }
}
