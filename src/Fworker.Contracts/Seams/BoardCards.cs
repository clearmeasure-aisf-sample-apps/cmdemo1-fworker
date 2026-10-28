using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;

namespace Fworker.Contracts.Seams;

// W4.3: what the board reconciler reads. A card is keyed by the repository of its issue, not by the home repository: v1's Get-BoardList drops every card of another
// repository before its filter, the reconciler keeps the cards of every repository of the catalog (cmdemo1-system's cards among them).

/// <summary>One card of the project board: the issue it holds, in the Status column it stands in (null when it has none).</summary>
public sealed record BoardCard(RepoId Repository, int Number, string? Column, bool Open, string Title);

/// <summary>The read side of the board the reconciler needs: one paged read of every card with an issue. Failure throws (a poll that failed is tried again by the next).</summary>
public interface IBoardReader
{
    Task<IReadOnlyList<BoardCard>> Read(BoardRef board, CancellationToken ct = default);
}
