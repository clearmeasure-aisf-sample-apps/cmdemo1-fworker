using System.Diagnostics.CodeAnalysis;

namespace Fworker.Contracts.Ids;

/// <summary>A repository, <c>owner/repo</c>, lowercased (GitHub treats owner and repository names case-insensitively, hook H1 of docs/design/system-gateway.md 3).</summary>
public readonly record struct RepoId
{
    public string Owner { get; }
    public string Repo { get; }

    public RepoId(string owner, string repo)
    {
        if (!ItemId.IsValidName(owner)) throw new ArgumentException($"'{owner}' is not a GitHub owner name", nameof(owner));
        if (!ItemId.IsValidName(repo)) throw new ArgumentException($"'{repo}' is not a GitHub repository name", nameof(repo));
        Owner = owner.ToLowerInvariant();
        Repo = repo.ToLowerInvariant();
    }

    public static RepoId Parse(string text) => TryParse(text, out var id) ? id : throw new FormatException($"'{text}' is not 'owner/repo'");

    public static bool TryParse([NotNullWhen(true)] string? text, out RepoId id)
    {
        id = default;
        if (text is null) return false;
        var parts = text.Trim().Split('/');
        if (parts.Length != 2 || !ItemId.IsValidName(parts[0]) || !ItemId.IsValidName(parts[1])) return false;
        id = new RepoId(parts[0], parts[1]);
        return true;
    }

    /// <summary><c>owner/repo</c>, lowercase.</summary>
    public override string ToString() => $"{Owner}/{Repo}";
}

/// <summary>
/// The qualified id of a work item: repository and issue number. The text form people read is <c>owner/repo#N</c> (GitHub links it); a pull request is
/// <c>owner/repo!N</c> internally. The owner and the repository are lowercased on construction, so equal ids compare equal whatever the case that came in.
/// A bare <c>#N</c> means the home repository and is parsed only through the overloads that take the home. The default value is not a valid id.
/// </summary>
public readonly record struct ItemId
{
    public RepoId Repository { get; }
    public int Number { get; }

    public ItemId(RepoId repository, int number)
    {
        if (repository.Owner is null) throw new ArgumentException("the repository is not set", nameof(repository));
        if (number < 1) throw new ArgumentOutOfRangeException(nameof(number), number, "an issue number is 1 or more");
        Repository = repository;
        Number = number;
    }

    public ItemId(string owner, string repo, int number) : this(new RepoId(owner, repo), number) { }

    /// <summary>Parse <c>owner/repo#N</c> (any case). Throws <see cref="FormatException"/> for anything else, a bare <c>#N</c> included.</summary>
    public static ItemId Parse(string text) => TryParse(text, out var id) ? id : throw new FormatException($"'{text}' is not 'owner/repo#N'");

    /// <summary>Parse <c>owner/repo#N</c>, or a bare <c>#N</c> as an item of <paramref name="home"/>.</summary>
    public static ItemId Parse(string text, RepoId home) => TryParse(text, home, out var id) ? id : throw new FormatException($"'{text}' is not 'owner/repo#N' or '#N'");

    public static bool TryParse([NotNullWhen(true)] string? text, out ItemId id) => TryParseCore(text, '#', null, out id);

    public static bool TryParse([NotNullWhen(true)] string? text, RepoId home, out ItemId id) => TryParseCore(text, '#', home, out id);

    /// <summary>Parse the internal pull request form <c>owner/repo!N</c>.</summary>
    public static bool TryParsePullRequest([NotNullWhen(true)] string? text, out ItemId id) => TryParseCore(text, '!', null, out id);

    static bool TryParseCore(string? text, char sep, RepoId? home, out ItemId id)
    {
        id = default;
        if (text is null) return false;
        text = text.Trim();
        var at = text.IndexOf(sep);
        if (at < 0 || at != text.LastIndexOf(sep)) return false;
        var number = text[(at + 1)..];
        // digits only: no sign, no space, no leading zero, at most seven digits (like the Depends on parser of the rule scripts)
        if (number.Length is < 1 or > 7 || number[0] == '0' || !number.All(char.IsAsciiDigit)) return false;
        var n = int.Parse(number);
        RepoId repo;
        if (at == 0) { if (home is not { } h) return false; repo = h; }
        else if (!RepoId.TryParse(text[..at], out repo)) return false;
        id = new ItemId(repo, n);
        return true;
    }

    /// <summary><c>owner/repo#N</c>, lowercase: every text people read, and the key of messages, sagas and the correlation map.</summary>
    public override string ToString() => $"{Repository}#{Number}";

    /// <summary>The pull request form <c>owner/repo!N</c> (internal use).</summary>
    public string ToPullRequestString() => $"{Repository}!{Number}";

    /// <summary>GitHub names: letters, digits, '-', '_' and '.', not empty, at most 100 characters; no '/', '#', '!' or white space.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 100 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
