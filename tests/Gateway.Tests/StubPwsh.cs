namespace Gateway.Tests;

/// <summary>
/// A stand-in for the rule scripts: an executable /bin/sh script named <c>pwsh</c>, first on the PATH, so the gateway's Runner
/// (<c>pwsh -NoProfile -NonInteractive -File &lt;entry&gt; --server &lt;verb&gt;</c>) reaches it instead of PowerShell. Each call appends
/// <c>&lt;ns-time&gt;\t&lt;verb&gt;\t&lt;action&gt;\t&lt;final&gt;</c> to the log (action: the first "action" of the input, final: whether the
/// input holds "final":true) and answers a fixed JSON. transcript-save fails until the file <c>transcript-ok</c> exists in the stub's directory.
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
        case "$verb" in
          revocations) echo '{"ok":true,"revoked":{}}' ;;
          leader-lease) echo '{"ok":true,"epoch":1,"leader":true}' ;;
          plant-snapshot-load|plant-snapshot-await) echo '{"ok":true,"found":false}' ;;
          transcript-save)
            if [ -e "$FWTEST_DIR/transcript-ok" ]; then echo '{"ok":true}'; else echo '{"ok":false,"code":"stub-not-yet"}'; exit 1; fi ;;
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
}
