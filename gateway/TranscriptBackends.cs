using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Workflows.Transcripts;

// Where the TranscriptStore keeps the sessions it holds in memory (W9.8). The store builds a session's document (the shape the ref store always held: name, item, phase, runtime, day, started, ended, lastLine, dropped,
// total, lines) and hands it to its backend; the backend saves it, reads it back by name and prunes by age. Two backends:
//  - RuleScriptTranscriptBackend: v1's, the rule scripts transcript-save/-load/-prune over the plant snapshot's ref store. The document is sealed first (the repository is public).
//  - DatabaseTranscriptBackend: where the engine acts, factory.Transcripts. No seal (the database is the gateway's own), the qualified item as a column, and the rule-script backend as a fallback for a session
//    that was written before the move (read once, saved to the database from then on; the prune clears the old refs as they age out).

static class TranscriptJson
{
    /// <summary>A number of a document, whichever width it was made with (a node built in memory keeps its type; a parsed one converts); 0 when it is not one.</summary>
    public static long Num(JsonNode? n) => n is JsonValue v ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (long)d : 0 : 0;
}

interface ITranscriptBackend
{
    /// <summary>Stores the session; false when it was not taken (the store keeps it dirty and tries again).</summary>
    Task<bool> Save(JsonObject doc);
    /// <summary>The session of that name as a document, or null.</summary>
    Task<JsonObject?> Load(string name);
    /// <summary>Removes the sessions of the days before the last <paramref name="days"/>.</summary>
    Task Prune(int days);
}

/// <summary>The sealed form of a transcript (gzip, then AES-GCM with a key derived with HKDF from the gateway's token secret): "fwt1:" and base64 of nonce, tag and cipher.</summary>
sealed class TranscriptSeal
{
    readonly byte[] key;
    public TranscriptSeal(string tokenSecret) => key = tokenSecret.Length == 0 ? [] : HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(tokenSecret), 32, info: Encoding.UTF8.GetBytes("fworker-transcript-v1"));
    public bool Available => key.Length > 0;

    public string Seal(JsonObject doc)
    {
        var plain = Encoding.UTF8.GetBytes(doc.ToJsonString());
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, true)) gz.Write(plain);
        var packed = ms.ToArray();
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[packed.Length];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, packed, cipher, tag);
        return "fwt1:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public JsonObject? Open(string data)
    {
        try
        {
            if (!data.StartsWith("fwt1:", StringComparison.Ordinal) || key.Length == 0) return null;
            var b = Convert.FromBase64String(data[5..]);
            if (b.Length < 28) return null;
            var packed = new byte[b.Length - 28];
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(b.AsSpan(0, 12), b.AsSpan(28), b.AsSpan(12, 16), packed);
            using var gz = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
            using var rd = new StreamReader(gz, Encoding.UTF8);
            return JsonNode.Parse(rd.ReadToEnd()) as JsonObject;
        }
        catch { return null; }   // another secret, or changed by someone with write access: not this gateway's transcript
    }
}

sealed class RuleScriptTranscriptBackend(Func<string, string, TimeSpan, Task<(int Code, string Output)>> run, TranscriptSeal seal) : ITranscriptBackend
{
    string tree = "";

    public async Task<bool> Save(JsonObject doc)
    {
        // the stored message is capped at 900000 characters: older lines are left out of the stored copy until it fits
        var lines = (doc["lines"] as JsonArray ?? []).Select(l => l?.ToString() ?? "").ToList();
        var dropped = TranscriptJson.Num(doc["dropped"]);
        var skip = 0; string data;
        while (true)
        {
            var copy = new JsonObject();
            foreach (var (k, v) in doc) if (k is not ("lines" or "dropped")) copy[k] = v?.DeepClone();
            copy["dropped"] = dropped + skip; copy["lines"] = new JsonArray(lines.Skip(skip).Select(l => (JsonNode)l).ToArray());
            data = seal.Seal(copy);
            if (data.Length <= 880000 || skip >= lines.Count) break;
            skip += Math.Max(1, (lines.Count - skip) / 4);
        }
        var args = new JsonObject { ["name"] = doc["name"]!.ToString(), ["day"] = doc["day"]!.ToString(), ["data"] = data }; if (tree != "") args["tree"] = tree;
        var (code, output) = await run("transcript-save", args.ToJsonString(), TimeSpan.FromSeconds(60));
        if (code != 0) return false;
        if (JsonNode.Parse(output) is JsonObject o && o["tree"]?.ToString() is { Length: > 0 } t) tree = t;
        return true;
    }

    public async Task<JsonObject?> Load(string name)
    {
        var (code, output) = await run("transcript-load", new JsonObject { ["name"] = name }.ToJsonString(), TimeSpan.FromSeconds(60));
        return code == 0 && JsonNode.Parse(output) is JsonObject o && o["found"]?.GetValue<bool>() == true ? seal.Open(o["data"]?.ToString() ?? "") : null;
    }

    public async Task Prune(int days) => await run("transcript-prune", new JsonObject { ["days"] = days }.ToJsonString(), TimeSpan.FromSeconds(120));
}

/// <summary>The sessions as rows of factory.Transcripts. <paramref name="ready"/> says whether the schema is good (a start before the scripts ran is not a failure to log); <paramref name="legacy"/> is read for a session the database does not have.</summary>
sealed class DatabaseTranscriptBackend(ITranscriptRepository repository, Fworker.Contracts.Ids.RepoId? home, Func<bool> ready, ITranscriptBackend? legacy = null) : ITranscriptBackend
{
    public async Task<bool> Save(JsonObject doc)
    {
        if (!ready()) return false;
        await repository.Save(ToRow(doc, home));
        return true;
    }

    public async Task<JsonObject?> Load(string name)
    {
        if (ready() && await repository.Load(name) is { } row) return ToDoc(row);
        if (legacy == null) return null;
        try { return await legacy.Load(name); } catch (Exception e) { Console.Error.WriteLine($"transcript-load {name} (ref store): {e.GetType().Name}"); return null; }
    }

    public async Task Prune(int days)
    {
        if (ready()) await repository.Prune(days);
        if (legacy != null) { try { await legacy.Prune(days); } catch { } }   // the refs written before the move age out here
    }

    // the document of the store -> the row: the item is qualified with the home repository
    public static TranscriptDoc ToRow(JsonObject doc, Fworker.Contracts.Ids.RepoId? home)
    {
        var item = (int)TranscriptJson.Num(doc["item"]);
        DateTimeOffset At(string k, DateTimeOffset dflt) => DateTimeOffset.TryParse(doc[k]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var t) ? t : dflt;
        var started = At("started", DateTimeOffset.UtcNow);
        return new TranscriptDoc(doc["name"]!.ToString(), item > 0 && home is { } h ? $"{h}#{item}" : null, doc["runtime"]?.ToString() ?? "", doc["phase"]?.ToString() ?? "",
            started, At("lastLine", started), doc["ended"] is null ? null : At("ended", started), TranscriptJson.Num(doc["dropped"]), TranscriptJson.Num(doc["total"]),
            (doc["lines"] as JsonArray ?? []).Select(l => l?.ToString() ?? "").ToList());
    }

    // the row -> the document the store loads a session from
    public static JsonObject ToDoc(TranscriptDoc row)
    {
        var hash = row.ItemKey?.LastIndexOf('#') ?? -1;
        var item = hash >= 0 && int.TryParse(row.ItemKey![(hash + 1)..], CultureInfo.InvariantCulture, out var n) ? n : 0;
        return new JsonObject
        {
            ["v"] = 1, ["name"] = row.Name, ["item"] = item, ["phase"] = row.Phase, ["runtime"] = row.Runtime, ["day"] = row.Started.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            ["started"] = row.Started.UtcDateTime.ToString("o"), ["ended"] = row.Ended?.UtcDateTime.ToString("o"), ["lastLine"] = row.LastLine.UtcDateTime.ToString("o"),
            ["dropped"] = row.Dropped, ["total"] = row.Total, ["lines"] = new JsonArray(row.Lines.Select(l => (JsonNode)l).ToArray()),
        };
    }
}
