namespace Gateway.Tests;

/// <summary>
/// A stand-in for the rule scripts: an executable /bin/sh script named <c>pwsh</c>, first on the PATH, so the gateway's Runner
/// (<c>pwsh -NoProfile -NonInteractive -File &lt;entry&gt; --server &lt;verb&gt;</c>) reaches it instead of PowerShell. Each call appends
/// <c>&lt;ns-time&gt;\t&lt;verb&gt;\t&lt;action&gt;\t&lt;final&gt;</c> to the log (action: the first "action" of the input, final: whether the
/// input holds "final":true) and answers a fixed JSON. transcript-save fails until the file <c>transcript-ok</c> exists in the stub's directory;
/// item-pr answers the file <c>item-pr.json</c> there when it exists, with the exit code in <c>item-pr.exit</c>.
/// Any verb answers a file of the test's own when one exists, the most specific first: <c>answer-&lt;verb&gt;-&lt;action&gt;-&lt;item&gt;.json</c>,
/// <c>answer-&lt;verb&gt;-&lt;action&gt;.json</c>, <c>answer-&lt;verb&gt;-&lt;item&gt;.json</c> (for a verb whose input has no action),
/// <c>answer-&lt;verb&gt;.json</c> (item: the first "item" number of the input), with the exit code in
/// the same name ending <c>.exit</c> (default 0). Every call's whole input is also appended to <c>&lt;log&gt;.in</c> as
/// <c>&lt;verb&gt;\t&lt;input&gt;</c> (the Runner writes compact JSON: one line).
/// Linux and macOS only.
/// </summary>
public static class StubPwsh
{
    /// <summary>Whether the stub can run here (a POSIX shell).</summary>
    public static bool Supported => !OperatingSystem.IsWindows();

    const string Script = """
        #!/bin/sh
        verb="$6"
        input=$(cat)
        action=$(printf '%s' "$input" | grep -o '"action":"[^"]*"' | head -n 1 | sed 's/^"action":"//; s/"$//')
        if printf '%s' "$input" | grep -q '"final":true'; then final=true; else final=false; fi
        printf '%s\t%s\t%s\t%s\n' "$(date +%s%N)" "$verb" "$action" "$final" >> "$FWTEST_PWSH_LOG"
        printf '%s\t%s\n' "$verb" "$input" >> "$FWTEST_PWSH_LOG.in"
        item=$(printf '%s' "$input" | grep -o '"item":[0-9]*' | head -n 1 | sed 's/^"item"://')
        for a in "answer-$verb-$action-$item" "answer-$verb-$action" "answer-$verb-$item" "answer-$verb"; do
          if [ -e "$FWTEST_DIR/$a.json" ]; then
            cat "$FWTEST_DIR/$a.json"
            if [ -e "$FWTEST_DIR/$a.exit" ]; then exit "$(cat "$FWTEST_DIR/$a.exit")"; fi
            exit 0
          fi
        done
        case "$verb" in
          revocations) echo '{"ok":true,"revoked":{}}' ;;
          leader-lease) echo '{"ok":true,"epoch":1,"leader":true}' ;;
          plant-snapshot-load|plant-snapshot-await) echo '{"ok":true,"found":false}' ;;
          transcript-save)
            if [ -e "$FWTEST_DIR/transcript-ok" ]; then echo '{"ok":true}'; else echo '{"ok":false,"code":"stub-not-yet"}'; exit 1; fi ;;
          item-pr)
            if [ -e "$FWTEST_DIR/item-pr.json" ]; then cat "$FWTEST_DIR/item-pr.json"; else echo '{"ok":true}'; fi
            if [ -e "$FWTEST_DIR/item-pr.exit" ]; then exit "$(cat "$FWTEST_DIR/item-pr.exit")"; fi ;;
          *) echo '{"ok":true}' ;;
        esac
        exit 0
        """;

    /// <summary>Writes the stub into <paramref name="dir"/> and returns that directory (to put first on the PATH).</summary>
    public static string Install(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "pwsh");
        File.WriteAllText(path, Script.Replace("\r\n", "\n") + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        return dir;
    }

    /// <summary>One call the stub logged.</summary>
    public sealed record Call(string Verb, string Action, bool Final);

    /// <summary>The calls in the log, in the order they were written.</summary>
    public static IReadOnlyList<Call> Read(string log)
    {
        if (!File.Exists(log)) return Array.Empty<Call>();
        return File.ReadAllLines(log).Select(l => l.Split('\t')).Where(p => p.Length >= 4).Select(p => new Call(p[1], p[2], p[3] == "true")).ToList();
    }

    /// <summary>One call's whole input, as the stub read it.</summary>
    public sealed record Input(string Verb, string Text);

    /// <summary>The inputs of the calls of <paramref name="verb"/> (every verb when null), in the order they were written.</summary>
    public static IReadOnlyList<Input> Inputs(string log, string? verb = null)
    {
        var path = log + ".in";
        if (!File.Exists(path)) return Array.Empty<Input>();
        string[] lines;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var rd = new StreamReader(fs)) lines = rd.ReadToEnd().Split('\n');
        return lines.Select(l => l.Split('\t', 2)).Where(p => p.Length == 2 && (verb == null || p[0] == verb)).Select(p => new Input(p[0], p[1])).ToList();
    }

    /// <summary>Makes the stub answer <paramref name="json"/> with exit <paramref name="exit"/> for <c>answer-&lt;name&gt;</c> (see the class).</summary>
    public static void Answer(string dir, string name, string json, int exit = 0)
    {
        File.WriteAllText(Path.Combine(dir, $"answer-{name}.json"), json + "\n");
        var exitPath = Path.Combine(dir, $"answer-{name}.exit");
        if (exit != 0) File.WriteAllText(exitPath, exit.ToString()); else File.Delete(exitPath);
    }
}
