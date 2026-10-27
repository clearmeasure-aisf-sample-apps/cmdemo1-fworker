using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// from_message on append-body and comment (task C8, gateway-composition.md 3 F; worker-messaging.md 3.8), moved verbatim out of the verb route:
// the id must be a message the caller sent or received; the text loses backticks and HTML comment markers, is capped at 500 characters and becomes one
// marked, quoted block before the rule script runs. Without from_message the literal marker is refused, so a session can neither forge a block nor
// close one early. Pure: the plant's lookup of a message comes in as a function. tests/Gateway.Tests/DerivedTextRuleTests.cs.
static class DerivedTextRule
{
    // What the plant knows of a message for a session: whether it sent or received it, its source, its sender and the question it answers ("" if none)
    public delegate (bool Known, string Source, string From, string InReplyTo) MessageParty(string messageId, string session);

    // The verb's body field that carries the text
    public static string FieldOf(string verb) => verb == "append-body" ? "content" : "body";

    // Returns the refusal, or null when the call goes on (the body is rewritten in place when it carried from_message).
    public static PolicyRefusal? Apply(string verb, JsonObject body, string callerKind, string callerName, MessageParty party)
    {
        var field = FieldOf(verb);
        var text = body[field] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : null;
        if (!body.ContainsKey("from_message") || body["from_message"] is null)
            return text != null && text.Contains("aisf:assumption", StringComparison.OrdinalIgnoreCase)
                ? new(409, "reserved-marker", "the text carries the marker aisf:assumption, which only the gateway writes: text that comes from a message goes in with --from-message <id>", 10) : null;
        var mid = body["from_message"]!.ToString();
        if (callerKind != "item" || text == null || !Regex.IsMatch(mid, @"^m-[0-9a-f]{8}-\d{4,}\z"))
            return new(400, "usage", "from_message is the id of a message this session sent or received, given with the session's item token and the text", 2);
        var (known, source, from, inReplyTo) = party(mid, callerName);
        if (!known) return new(404, "no-such-message", "from_message names no message that this session sent or received in this run of the gateway", 12);
        var clean = text.Replace("`", "").Replace("\r", "");
        while (clean.Contains("<!--") || clean.Contains("-->")) clean = clean.Replace("<!--", "").Replace("-->", "");
        var capped = new StringBuilder(); var count = 0;
        foreach (var rune in clean.Trim().EnumerateRunes()) { if (count++ >= 500) break; capped.Append(rune.ToString()); }
        clean = capped.ToString().Trim();
        if (clean.Length == 0) return new(400, "usage", "there is no text left to write: send the assumption or the decision in your own words", 2);
        // the block names the message whose text it carries and that message's sender; for an answer also the question it answers
        body[field] = $"<!-- aisf:assumption source={source} from={from}{(inReplyTo == "" ? "" : " in_reply_to=" + inReplyTo)} -->\n" + string.Join("\n", clean.Split('\n').Select(l => "> " + l)) + "\n<!-- /aisf:assumption -->";
        body.Remove("from_message");
        return null;
    }
}
