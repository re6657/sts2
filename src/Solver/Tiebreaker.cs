using System;
using System.Collections.Generic;
using System.Linq;

namespace TokenSpire2.Solver;

/// <summary>
/// Random tiebreaker for scored options.
/// When multiple options have equal (or very close) scores, picks one randomly
/// instead of always picking the first — avoiding deterministic bias.
///
/// In multiplayer mode, replaces randomness with a stable hash-based pick
/// so ALL instances produce the same result for the same input — preventing
/// StateDivergence disconnections caused by local RNG desync.
/// </summary>
public static class Tiebreaker
{
    /// <summary>
    /// 2% relative or 3.0 absolute, whichever is larger.
    /// Scores within this range of the best score are treated as tied.
    /// </summary>
    private const double RELATIVE_EPSILON = 0.02;
    private const double ABSOLUTE_EPSILON = 3.0;

    private static readonly Random _rng = new();

    /// <summary>
    /// Set to true during multiplayer (LAN/online) runs.
    /// When true, tiebreaking uses a stable hash of the tied items rather
    /// than System.Random, ensuring all peers pick the same winner and
    /// preventing state divergence / checksum mismatches.
    /// </summary>
    public static bool InMultiplayerMode { get; set; }

    // ── Deterministic hash for multiplayer tiebreaking ────────────────────

    /// <summary>
    /// Compute a stable, platform-independent hash from the string
    /// representations of all tied items. Uses FNV-1a algorithm rather
    /// than string.GetHashCode() because the latter is NOT guaranteed
    /// stable across .NET versions or process runs.
    /// </summary>
    private static int DeterministicIndex<T>(IReadOnlyList<T> items)
    {
        if (items.Count <= 1) return 0;
        uint hash = 2166136261; // FNV-1a 32-bit offset basis
        foreach (var item in items)
        {
            string s = item?.ToString() ?? "";
            foreach (char c in s)
                hash = unchecked((hash ^ c) * 16777619); // FNV-1a prime
        }
        return (int)(hash % (uint)items.Count);
    }

    // ── Core: pick from an ALREADY-SORTED list (descending by score) ──────

    /// <summary>
    /// From a list already sorted descending by score, pick the best element.
    /// If multiple elements have scores within epsilon of the top score,
    /// randomly select among them (deterministic hash in multiplayer).
    /// </summary>
    public static T PickBestFromSorted<T>(List<T> scoredDescending, Func<T, double> getScore)
    {
        if (scoredDescending.Count == 0)
            throw new InvalidOperationException("Tiebreaker: empty list");

        double topScore = getScore(scoredDescending[0]);
        double threshold = Math.Max(Math.Abs(topScore) * RELATIVE_EPSILON, ABSOLUTE_EPSILON);

        // Collect all items within the tie threshold of the top score
        var tied = scoredDescending
            .TakeWhile(x => (topScore - getScore(x)) <= threshold)
            .ToList();

        if (tied.Count == 1)
            return tied[0];

        int pick = InMultiplayerMode
            ? DeterministicIndex(tied)
            : _rng.Next(tied.Count);
        return tied[pick];
    }

    // ── Convenience: score + sort + pick in one call ──────────────────────

    /// <summary>
    /// Score items, sort descending, and pick best with random tiebreaking.
    /// </summary>
    public static T PickBest<T>(IEnumerable<T> items, Func<T, double> scorer)
    {
        var scored = items
            .Select(item => (item, score: scorer(item)))
            .OrderByDescending(x => x.score)
            .ToList();

        if (scored.Count == 0)
            throw new InvalidOperationException("Tiebreaker.PickBest: empty enumerable");

        return PickBestFromSorted(scored, x => x.score).item;
    }

    /// <summary>
    /// Pick best and return (item, score).
    /// </summary>
    public static (T item, double score) PickBestScored<T>(
        IEnumerable<T> items, Func<T, double> scorer)
    {
        var scored = items
            .Select(item => (item, score: scorer(item)))
            .OrderByDescending(x => x.score)
            .ToList();

        if (scored.Count == 0)
            throw new InvalidOperationException("Tiebreaker.PickBestScored: empty enumerable");

        return PickBestFromSorted(scored, x => x.score);
    }

    // ── ShopDecider: priority-tier + score tiebreaking ────────────────────

    /// <summary>
    /// Items have a priority (lower=better) and score (higher=better).
    /// Within the best priority tier, pick best score with random tiebreaking.
    /// </summary>
    public static T PickBestByPriority<T>(
        List<T> items,
        Func<T, int> getPriority,
        Func<T, double> getScore)
    {
        if (items.Count == 0)
            throw new InvalidOperationException("Tiebreaker.PickBestByPriority: empty list");

        int bestPriority = items.Min(getPriority);

        var inTier = items
            .Where(item => getPriority(item) == bestPriority)
            .Select(item => (item, score: getScore(item)))
            .OrderByDescending(x => x.score)
            .ToList();

        return PickBestFromSorted(inTier, x => x.score).item;
    }

    // ── Ascending sort (lower=better): for transform/remove decisions ─────

    /// <summary>
    /// Items sorted ascending by score (lower=better). Pick the lowest with
    /// random tiebreaking among near-tied minimum scores.
    /// </summary>
    public static T PickBestAscending<T>(IEnumerable<T> items, Func<T, double> scorer)
    {
        var scored = items
            .Select(item => (item, score: scorer(item)))
            .OrderBy(x => x.score)
            .ToList();

        if (scored.Count == 0)
            throw new InvalidOperationException("Tiebreaker.PickBestAscending: empty enumerable");

        double bestScore = scored[0].score;
        double threshold = Math.Max(Math.Abs(bestScore) * RELATIVE_EPSILON, ABSOLUTE_EPSILON);

        var tied = scored
            .TakeWhile(x => (x.score - bestScore) <= threshold)
            .ToList();

        if (tied.Count == 1)
            return tied[0].item;

        int pick = InMultiplayerMode
            ? DeterministicIndex(tied)
            : _rng.Next(tied.Count);
        return tied[pick].item;
    }

    /// <summary>
    /// From an already ascending-sorted list, pick with random tiebreaking.
    /// </summary>
    public static T PickBestFromSortedAscending<T>(
        List<T> scoredAscending, Func<T, double> getScore)
    {
        if (scoredAscending.Count == 0)
            throw new InvalidOperationException("Tiebreaker: empty list");

        double bestScore = getScore(scoredAscending[0]);
        double threshold = Math.Max(Math.Abs(bestScore) * RELATIVE_EPSILON, ABSOLUTE_EPSILON);

        var tied = scoredAscending
            .TakeWhile(x => (getScore(x) - bestScore) <= threshold)
            .ToList();

        if (tied.Count == 1)
            return tied[0];

        int pick = InMultiplayerMode
            ? DeterministicIndex(tied)
            : _rng.Next(tied.Count);
        return tied[pick];
    }
}
