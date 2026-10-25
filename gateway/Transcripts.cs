using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// Session transcripts of runtime workers (the owner's requirement of 2026-10-23: a link to the AI session in the item's comments while it is
// worked). A container of the Octopus or Container Apps runtime (runner/fwrunner.sh) posts the redacted lines it prints, in batches, with its own
// credential (POST /api/v1/runtime/transcript); the operator reads them at /sessions/{name} (live while the session runs) and
// GET /api/v1/sessions/{name}.
//
// Durable store: the one the plant snapshot already uses (rule scripts transcript-save/-load/-prune, plugin/scripts/lib/PlantSnapshot.ps1): a
// parentless commit per save in the work-orders repository, one ref per session under refs/factory/<instance>/transcripts/<yyyymmdd>/<name>.
// The gateway holds no disk and no blob store of its own, and GitHub is what it already writes to and reads back at a start. That repository is
// public, so the message is ciphertext: gzip of the JSON, then AES-GCM with a key derived (HKDF) from the gateway's token secret, which the
// environment's vault keeps across restarts and releases. Kept: the last MaxBytes of lines per session, sessions of the last Days days.
// Saved at most every SaveSeconds while lines arrive, at once when the session ends, and at the stop of the process.
sealed class TranscriptStore
{
    public int MaxBytes = 2 * 1024 * 1024, MaxLine = 2000, MaxBatchLines = 500, Days = 14, MaxSessions = 200;
    public double SaveSeconds = 60;

    sealed class Session
    {
        public string Name = "", Runtime = "", Phase = ""; public int Item; public DateTime Started, LastLine; public DateTime? Ended;
        public readonly LinkedList<string> Lines = new(); public long Bytes, Dropped, Total; public bool Dirty; public DateTime SavedAt; public string Day = "";
    }

    readonly object gate = new();
    readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTime> missing = new(StringComparer.Ordinal);
    readonly Func<string, string, TimeSpan, Task<(int Code, string Output)>>? run;
    readonly byte[] key;
    string tree = "";
    DateTime prunedAt = DateTime.MinValue;
    long saves, saveFailures, loads;
    int saving;

    public bool Durable => run != null && key.Length > 0;

    public TranscriptStore(Func<string, string, TimeSpan, Task<(int Code, string Output)>>? run, string tokenSecret)
    {
        this.run = run;
        key = tokenSecret.Length == 0 ? [] : HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(tokenSecret), 32, info: Encoding.UTF8.GetBytes("fworker-transcript-v1"));
    }

    public static bool ValidName(string n) => n.Length is > 0 and <= 100 && char.IsAsciiLetterOrDigit(n[0]) && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    // a runtime started a session: its item and phase for the page; the lines come from the container
    public void Started(string name, int item, string phase, string runtime)
    {
        if (!ValidName(name)) return;
        lock (gate)
        {
            // a name used again after its session ended starts a fresh transcript (the old one stays in the store until it is saved over)
            if (sessions.TryGetValue(name, out var old) && old.Ended != null) sessions.Remove(name);
            if (Get(name, true) is not { } s) return;
            s.Item = item; s.Phase = phase; s.Runtime = runtime; s.Ended = null; s.Dirty = true;
        }
    }

    // whether this gateway holds a transcript of the session (in memory: started or loaded since its start)
    public bool Has(string name) { lock (gate) return sessions.ContainsKey(name); }
    public void Ended(string name) { lock (gate) if (sessions.TryGetValue(name, out var s) && s.Ended == null) { s.Ended = DateTime.UtcNow; s.Dirty = true; s.SavedAt = DateTime.MinValue; } }

    Session? Get(string name, bool create)
    {
        if (sessions.TryGetValue(name, out var s)) return s;
        if (!create) return null;
        if (sessions.Count >= MaxSessions)
        {   // durable: the oldest saved one makes room, and with none saved the new session is refused (null) until a flush; in memory only
            // (nothing is ever saved): the oldest one makes room whatever it holds
            var old = sessions.Values.Where(x => !Durable || !x.Dirty).OrderBy(x => x.Ended ?? x.LastLine).FirstOrDefault();
            if (old == null) return null;
            sessions.Remove(old.Name);
        }
        var now = DateTime.UtcNow;
        s = new Session { Name = name, Started = now, LastLine = now, Day = now.ToString("yyyyMMdd") };
        sessions[name] = s; return s;
    }

    // the lines of a batch, each shortened to MaxLine; the session keeps its last MaxBytes. Answers how many were taken.
    public async Task<(int Taken, long Total)> Append(string name, IReadOnlyList<string> lines)
    {
        await EnsureLoaded(name);
        lock (gate)
        {
            if (Get(name, true) is not { } s) return (0, -1);
            var n = 0;
            foreach (var raw in lines.Take(MaxBatchLines))
            {
                var l = raw.Length > MaxLine ? raw[..MaxLine] + " ..." : raw;
                s.Lines.AddLast(l); s.Bytes += l.Length + 1; s.Total++; n++;
                while (s.Bytes > MaxBytes && s.Lines.First != null) { s.Bytes -= s.Lines.First.Value.Length + 1; s.Lines.RemoveFirst(); s.Dropped++; }
            }
            s.LastLine = DateTime.UtcNow; s.Dirty = true;
            return (n, s.Total);
        }
    }

    // a session this process does not hold is read back from the store once (a restart, or the page of an older session)
    async Task EnsureLoaded(string name)
    {
        lock (gate)
        {
            if (sessions.ContainsKey(name) || !Durable) return;
            // a name the store did not have is not asked for again for a minute (the page polls)
            if (missing.TryGetValue(name, out var m) && DateTime.UtcNow - m < TimeSpan.FromMinutes(1)) return;
            if (missing.Count > 1000) missing.Clear();
        }
        JsonObject? doc = null;
        try
        {
            var (code, output) = await run!("transcript-load", new JsonObject { ["name"] = name }.ToJsonString(), TimeSpan.FromSeconds(60));
            if (code == 0 && JsonNode.Parse(output) is JsonObject o && o["found"]?.GetValue<bool>() == true) doc = Open(o["data"]?.ToString() ?? "");
            Interlocked.Increment(ref loads);
        }
        catch (Exception e) { Console.Error.WriteLine($"transcript-load {name}: {e.GetType().Name}"); }
        if (doc == null || doc["name"]?.ToString() != name) { lock (gate) missing[name] = DateTime.UtcNow; return; }
        lock (gate)
        {
            if (sessions.ContainsKey(name)) return;
            if (Get(name, true) is not { } s) return;
            s.Item = (int?)doc["item"] ?? 0; s.Phase = doc["phase"]?.ToString() ?? ""; s.Runtime = doc["runtime"]?.ToString() ?? ""; s.Day = doc["day"]?.ToString() ?? s.Day;
            if (DateTime.TryParse(doc["started"]?.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var st)) s.Started = st;
            if (DateTime.TryParse(doc["ended"]?.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var en)) s.Ended = en;
            if (DateTime.TryParse(doc["lastLine"]?.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var ll)) s.LastLine = ll;
            s.Dropped = (long?)doc["dropped"] ?? 0; s.Total = (long?)doc["total"] ?? 0;
            foreach (var l in doc["lines"]?.AsArray() ?? []) { var t = l?.ToString() ?? ""; s.Lines.AddLast(t); s.Bytes += t.Length + 1; }
            s.Dirty = false; s.SavedAt = DateTime.UtcNow;
        }
    }

    public async Task<JsonObject?> Read(string name, int after)
    {
        if (!ValidName(name)) return null;
        await EnsureLoaded(name);
        lock (gate)
        {
            if (!sessions.TryGetValue(name, out var s)) return null;
            var first = s.Total - s.Lines.Count;   // the number of the first line held (lines are numbered from 0 over the whole session)
            var skip = (int)Math.Clamp(after - first, 0, s.Lines.Count);
            var live = s.Ended == null && DateTime.UtcNow - s.LastLine < TimeSpan.FromMinutes(30);
            return new JsonObject
            {
                ["ok"] = true, ["name"] = s.Name, ["item"] = s.Item, ["phase"] = s.Phase, ["runtime"] = s.Runtime, ["started"] = s.Started.ToString("o"),
                ["ended"] = s.Ended?.ToString("o"), ["lastLine"] = s.LastLine.ToString("o"), ["live"] = live, ["total"] = s.Total, ["dropped"] = s.Dropped, ["first"] = first + skip,
                ["durable"] = Durable, ["lines"] = new JsonArray(s.Lines.Skip(skip).Select(l => (JsonNode)l).ToArray())
            };
        }
    }

    public JsonObject Status() { lock (gate) return new JsonObject { ["durable"] = Durable, ["sessions"] = sessions.Count, ["dirty"] = sessions.Values.Count(s => s.Dirty), ["saving"] = Volatile.Read(ref saving), ["saves"] = saves, ["saveFailures"] = saveFailures, ["loads"] = loads, ["days"] = Days, ["maxBytes"] = MaxBytes }; }

    // ---- the durable copy ----------------------------------------------------------------------------------------------------------------------
    string Seal(JsonObject doc)
    {
        var plain = Encoding.UTF8.GetBytes(doc.ToJsonString());
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, true)) gz.Write(plain);
        var packed = ms.ToArray();
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[packed.Length];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, packed, cipher, tag);
        return "fwt1:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    JsonObject? Open(string data)
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

    JsonObject Doc(Session s, int skip) => new()
    {
        ["v"] = 1, ["name"] = s.Name, ["item"] = s.Item, ["phase"] = s.Phase, ["runtime"] = s.Runtime, ["day"] = s.Day, ["started"] = s.Started.ToString("o"),
        ["ended"] = s.Ended?.ToString("o"), ["lastLine"] = s.LastLine.ToString("o"), ["dropped"] = s.Dropped + skip, ["total"] = s.Total,
        ["lines"] = new JsonArray(s.Lines.Skip(skip).Select(l => (JsonNode)l).ToArray())
    };

    // saves the sessions whose lines changed (at most every SaveSeconds, an ended one at once); all: every changed one now (the process stops)
    public async Task Flush(bool all, TimeSpan timeout)
    {
        if (!Durable) return;
        List<(string Name, string Day, string Data)> work = [];
        lock (gate)
        {
            var now = DateTime.UtcNow;
            foreach (var s in sessions.Values.Where(x => x.Dirty && (all || x.Ended != null || (now - x.SavedAt).TotalSeconds >= SaveSeconds)).ToList())
            {
                // the stored message is capped at 900000 characters: older lines are left out of the stored copy until it fits
                var skip = 0; string data;
                while (true) { data = Seal(Doc(s, skip)); if (data.Length <= 880000 || skip >= s.Lines.Count) break; skip += Math.Max(1, (s.Lines.Count - skip) / 4); }
                work.Add((s.Name, s.Day, data)); s.Dirty = false; s.SavedAt = now; Interlocked.Increment(ref saving); // saving: taken and not yet answered, counted in the same lock as Dirty
            }
        }
        using var cts = new CancellationTokenSource(timeout);
        foreach (var (name, day, data) in work)
        {
            try {
            if (cts.IsCancellationRequested) { lock (gate) if (sessions.TryGetValue(name, out var s0)) s0.Dirty = true; continue; }
            var ok = false;
            try
            {
                var args = new JsonObject { ["name"] = name, ["day"] = day, ["data"] = data }; if (tree != "") args["tree"] = tree;
                var (code, output) = await run!("transcript-save", args.ToJsonString(), TimeSpan.FromSeconds(60));
                ok = code == 0;
                if (ok && JsonNode.Parse(output) is JsonObject o && o["tree"]?.ToString() is { Length: > 0 } t) tree = t;
            }
            catch (Exception e) { Console.Error.WriteLine($"transcript-save {name}: {e.GetType().Name}"); }
            if (ok) Interlocked.Increment(ref saves);
            else { Interlocked.Increment(ref saveFailures); lock (gate) if (sessions.TryGetValue(name, out var s1)) s1.Dirty = true; }
            } finally { Interlocked.Decrement(ref saving); }
        }
        // held in memory: an ended and saved session for an hour, then it is read back from the store when someone asks
        lock (gate) foreach (var s in sessions.Values.Where(x => !x.Dirty && x.Ended is { } e && DateTime.UtcNow - e > TimeSpan.FromHours(1)).ToList()) sessions.Remove(s.Name);
        if (DateTime.UtcNow - prunedAt > TimeSpan.FromHours(6))
        {
            prunedAt = DateTime.UtcNow;
            try { await run!("transcript-prune", new JsonObject { ["days"] = Days }.ToJsonString(), TimeSpan.FromSeconds(120)); } catch { }
        }
    }

    public async Task Loop(Func<bool> mayAct, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { break; }
            // only the acting process writes (two processes during a hand-over would write the same refs); a standby holds what it was sent
            try { if (mayAct()) await Flush(false, TimeSpan.FromSeconds(120)); } catch (Exception e) { Console.Error.WriteLine($"transcripts: {e.GetType().Name}"); }
        }
    }
}

// GET /sessions/{name}: the transcript of one session for the operator. The page holds no data: it reads /api/v1/sessions/{name} with the
// operator token, taken from a link like /sessions/<name>#token=<value> (removed from the address bar at once; the fragment never reaches the
// server) or from this tab's connection to the dashboard (sessionStorage ftok). While the session is live it asks for new lines every 5 s.
static class SessionPage
{
    public const string Html = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Session transcript</title>
<style>:root{color-scheme:light dark}body{font:14px system-ui,sans-serif;margin:16px;background:Canvas;color:CanvasText}h1{font-size:18px;margin:0 0 6px}
#meta{color:GrayText;margin-bottom:10px}#conn{margin:10px 0}#conn input{width:min(420px,90%)}pre{font:12px ui-monospace,monospace;white-space:pre-wrap;word-break:break-word;border:1px solid GrayText;padding:8px;margin:0}
.live{color:#1a7f37;font-weight:600}.mut{color:GrayText}</style></head><body>
<h1 id="title">Session</h1><div id="meta" class="mut">-</div>
<div id="conn" hidden><input id="tok" type="password" placeholder="operator token" autocomplete="off"> <button id="go">Connect</button></div>
<div id="msg" class="mut"></div><pre id="out"></pre>
<script>
const $=id=>document.getElementById(id);const name=decodeURIComponent(location.pathname.split('/').pop());$('title').textContent='Session '+name;document.title=name+' transcript';
{const m=/(?:^#|&)token=([^&]*)/.exec(location.hash);if(m){let v='';try{v=decodeURIComponent(m[1]).trim()}catch(e){}if(v)sessionStorage.setItem('ftok',v);history.replaceState(null,'',location.pathname+location.search)}}
let tok=sessionStorage.getItem('ftok')||'',next=0,timer=0;
$('go').onclick=()=>{const v=$('tok').value.trim();if(!v)return;tok=v;sessionStorage.setItem('ftok',v);$('tok').value='';$('conn').hidden=true;load()};
async function load(){clearTimeout(timer);if(!tok){$('conn').hidden=false;$('msg').textContent='Paste the operator token to read this transcript.';return}
try{const r=await fetch('/api/v1/sessions/'+encodeURIComponent(name)+'?after='+next,{headers:{Authorization:'Bearer '+tok}});
if(r.status===401||r.status===403){tok='';sessionStorage.removeItem('ftok');$('conn').hidden=false;$('msg').textContent='The token was refused.';return}
const j=await r.json();if(!r.ok){$('msg').textContent=j.reason||('HTTP '+r.status);timer=setTimeout(load,15000);return}
const atEnd=window.innerHeight+window.scrollY>=document.body.scrollHeight-40;
if(next===0&&j.first>0)$('out').textContent='... '+j.first+' earlier line(s) not kept (the last 2 MB are) ...\n';
else if(j.first>next)$('out').textContent+='... '+(j.first-next)+' line(s) not kept ...\n';
if(j.lines.length)$('out').textContent+=j.lines.join('\n')+'\n';next=j.first+j.lines.length;
$('meta').textContent=(j.live?'live · ':j.ended?'ended '+j.ended+' · ':'')+'item #'+j.item+' '+(j.phase||'')+' · '+(j.runtime||'')+' · started '+j.started+' · '+j.total+' line(s)';
$('meta').className=j.live?'live':'mut';$('msg').textContent='';if(atEnd&&j.lines.length)window.scrollTo(0,document.body.scrollHeight);
if(j.live)timer=setTimeout(load,5000)}catch(e){$('msg').textContent=e.message;timer=setTimeout(load,15000)}}
load();
</script></body></html>
""";
}
