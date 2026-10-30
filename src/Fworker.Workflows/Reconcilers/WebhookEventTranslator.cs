using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Workflows.Reconcilers;

// W9.8: the translation of GitHub's webhook payloads into the factory's events, ported from Verb_ingest_event of plugin/scripts/lib/Verbs.Sweep.ps1 (the rule script that sits in front of the ingress).
// The same events with the same fields for the same payload: issues (item.labeled, item.closed, item.reopened), workflow_run (build.branch_started/completed, build.master_started/completed),
// projects_v2_item (item.column_changed), pull_request (pr.opened, pr.head_changed, pr.merged, pr.closed, pr.drafted, base.merged) and pull_request_review (pr.reviewed). Anything else makes no event.
// PowerShell semantics are kept on purpose: -match, -eq and -ieq on text ignore case, a missing or null field reads as empty text and as 0 for a number, "$x" of a boolean is True or False.
// The two reads the script makes (the column of a labeled item, the issue behind a board card) are a port, so the rule needs no network.

/// <summary>The item's labels and Status column as the issue read of v1's Get-ItemInfo has them; Column is null when the item has no card.</summary>
public sealed record WebhookItemFacts(IReadOnlyList<string> Labels, string? Column);

/// <summary>The issue a board card stands for (the node of a projects_v2_item delivery): its number, repository (nameWithOwner) and labels.</summary>
public sealed record WebhookIssueNode(int Number, string Repository, IReadOnlyList<string> Labels);

public interface IWebhookLookups
{
    /// <summary>The item by number, or null when it does not exist. A failure throws.</summary>
    Task<WebhookItemFacts?> ReadItem(int item, CancellationToken ct = default);

    /// <summary>The issue behind a board card's content node, or null when the node is not an issue (a draft, a pull request, no node). A failure throws a <see cref="WebhookLookupException"/>.</summary>
    Task<WebhookIssueNode?> ReadIssueNode(string nodeId, CancellationToken ct = default);
}

/// <summary>A read the translation needs failed the way the rule script's Reject would: the exit code, the code and the reason of its answer.</summary>
public sealed class WebhookLookupException(int exit, string code, string reason, int? retryAfterSeconds = null) : Exception(reason)
{
    public int Exit { get; } = exit;
    public string Code { get; } = code;
    /// <summary>The seconds the answer asks the caller to wait; null for a refusal that has none (a missing credential).</summary>
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>The settings the translation reads: owner, repo, instance name (the branch prefix), default branch, the factory label and this environment's env label ("" when the environment is not scoped).</summary>
public sealed record WebhookTranslatorConfig(string Owner, string Repo, string Name, string DefaultBranch, string Label, string EnvLabel);

/// <summary>What one delivery made: the events, or the reason the delivery was dropped ("other-repository", "pr.fork") with no events.</summary>
public sealed record WebhookTranslation(IReadOnlyList<JsonObject> Events, string? Dropped = null);

public sealed class WebhookEventTranslator(WebhookTranslatorConfig config, IWebhookLookups lookups, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Regex branchOfItem = new($"^{Regex.Escape(config.Name)}/(\\d+)-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    string Full => $"{config.Owner}/{config.Repo}";

    public async Task<WebhookTranslation> Translate(string ev, string delivery, JsonNode? payload, CancellationToken ct = default)
    {
        var p = payload as JsonObject;
        var outEvents = new List<JsonObject>();
        if (Has(p, "repository") && !Eq(Str(p!["repository"]?["full_name"]), Full)) return new([], "other-repository");
        switch (ev)
        {
            case "issues":
            {
                var act = Str(p?["action"]); var num = Int(p?["issue"]?["number"]);
                var labels = Names(p?["issue"]?["labels"]);
                var mine = labels.Any(l => Eq(l, config.Label)) && InScope(labels);
                if (act == "labeled" && mine && (Eq(Str(p?["label"]?["name"]), config.Label) || Eq(Str(p?["label"]?["name"]), config.EnvLabel)))
                {
                    string? col = null;
                    try { if (await lookups.ReadItem(num, ct) is { } facts && InScope(facts.Labels)) col = facts.Column; } catch (Exception e) when (e is not OperationCanceledException) { }
                    outEvents.Add(Event("item.labeled", num, col, delivery));
                }
                else if (act is "closed" or "reopened" && mine) outEvents.Add(Event($"item.{act}", num, null, delivery));
                break;
            }
            case "workflow_run":
            {
                var run = p?["workflow_run"]; var act = Str(p?["action"]); var branch = Str(run?["head_branch"]);
                var kind = act == "completed" ? "completed" : act == "in_progress" ? "started" : null;
                if (kind != null)
                {
                    JsonObject Extra() => new() { ["sha"] = Str(run?["head_sha"]), ["conclusion"] = Copy(run?["conclusion"]), ["workflow"] = Str(run?["name"]), ["run_id"] = Has(run, "id") ? Copy(run!["id"]) : null, ["run_attempt"] = Has(run, "run_attempt") ? Copy(run!["run_attempt"]) : null };
                    var m = branchOfItem.Match(branch);
                    if (m.Success) outEvents.Add(Event($"build.branch_{kind}", Int(m.Groups[1].Value), null, delivery, Extra()));
                    else if (Eq(branch, config.DefaultBranch)) outEvents.Add(Event($"build.master_{kind}", null, null, delivery, Extra()));
                }
                break;
            }
            case "projects_v2_item":
            {
                if (Str(p?["action"]) == "edited" && Has(p, "changes") && Eq(Str(p!["changes"]?["field_value"]?["field_name"]), "Status"))
                {
                    var node = await lookups.ReadIssueNode(Str(p["projects_v2_item"]?["content_node_id"]), ct);
                    // the org-level webhook carries no repository: an issue of another repository linked to the board (or a draft or pull request, or no node) makes no event
                    if (node is not null && Eq(node.Repository, Full) && node.Labels.Any(l => Eq(l, config.Label)) && InScope(node.Labels))
                        outEvents.Add(Event("item.column_changed", node.Number, Str(p["changes"]?["field_value"]?["to"]?["name"]), delivery));
                }
                break;
            }
            case "pull_request":
            {
                var act = Str(p?["action"]); var pull = p?["pull_request"]; var branch = Str(pull?["head"]?["ref"]);
                // any pull request merged into the default branch moves master: the gateway tells the owners of open branches (base.updated)
                if (act == "closed" && Truthy(pull?["merged"]) && Has(pull, "base") && Str(pull!["base"]?["ref"]) == config.DefaultBranch)
                    outEvents.Add(Event("base.merged", null, null, delivery, new JsonObject { ["pr"] = Int(pull["number"]), ["sha"] = Str(pull["merge_commit_sha"]) }));
                var m = branchOfItem.Match(branch);
                if (m.Success)
                {
                    var num = Int(m.Groups[1].Value);
                    var full = Has(pull, "head") && Has(pull!["head"], "repo") ? Str(pull["head"]!["repo"]?["full_name"]) : "";
                    if (!Eq(full, Full)) return new([], "pr.fork");
                    JsonObject X() => new() { ["pr"] = Int(pull?["number"]), ["sha"] = Str(pull?["head"]?["sha"]), ["login"] = Has(p, "sender") ? Str(p!["sender"]?["login"]) : "" };
                    if (act is "opened" or "reopened" or "ready_for_review") outEvents.Add(Event("pr.opened", num, null, delivery, X()));
                    else if (act == "synchronize") outEvents.Add(Event("pr.head_changed", num, null, delivery, X()));
                    else if (act == "closed") outEvents.Add(Event(Truthy(pull?["merged"]) ? "pr.merged" : "pr.closed", num, null, delivery, X()));
                    else if (act == "converted_to_draft") outEvents.Add(Event("pr.drafted", num, null, delivery, X()));
                }
                break;
            }
            case "pull_request_review":
            {
                var act = Str(p?["action"]); var pull = p?["pull_request"];
                var m = branchOfItem.Match(Str(pull?["head"]?["ref"]));
                if (act is "submitted" or "dismissed" && m.Success)
                    outEvents.Add(Event("pr.reviewed", Int(m.Groups[1].Value), null, delivery, new JsonObject
                    {
                        ["pr"] = Int(pull?["number"]), ["sha"] = Str(pull?["head"]?["sha"]), ["login"] = Str(p?["review"]?["user"]?["login"]),
                        ["state"] = Str(p?["review"]?["state"]).ToUpperInvariant(), ["commit_id"] = Str(p?["review"]?["commit_id"]), ["action"] = act,
                    }));
                break;
            }
        }
        return new(outEvents);
    }

    // New-Event: provider, event, item, column, ref (the delivery), at; then the extra fields
    JsonObject Event(string name, int? item, string? column, string reference, JsonObject? extra = null)
    {
        var e = new JsonObject
        {
            ["provider"] = "github", ["event"] = name, ["item"] = item, ["column"] = column, ["ref"] = reference,
            ["at"] = now().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        };
        if (extra != null) foreach (var (k, v) in extra.ToList()) { extra.Remove(k); e[k] = v; }
        return e;
    }

    // Test-ItemInScope: an environment that is not scoped sees every item; a scoped one sees the items that carry exactly one env: label, its own
    bool InScope(IEnumerable<string> labels)
    {
        if (config.EnvLabel.Length == 0) return true;
        var envs = labels.Where(l => l.StartsWith("env:", StringComparison.OrdinalIgnoreCase)).ToList();
        return envs.Count == 1 && Eq(envs[0], config.EnvLabel);
    }

    // Test-Has: present and not null
    static bool Has(JsonNode? node, string key) => node is JsonObject o && o.TryGetPropertyValue(key, out var v) && v is not null;
    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // "$($x)": empty for a missing or null field, True or False for a boolean
    static string Str(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "True" : "False",
        JsonValue v => v.ToString(),
        _ => n.ToJsonString(),
    };

    // [int]$x
    static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? (int)l : int.TryParse(Str(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
    static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;

    // PowerShell truthiness of a field read from JSON: absent, null, false, 0 and "" are false
    static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        _ => true,
    };

    static JsonNode? Copy(JsonNode? n) => n?.DeepClone();
    static List<string> Names(JsonNode? labels) => (labels as JsonArray ?? []).Select(l => Str(l?["name"])).ToList();
}
