using System;
using System.Collections.Generic;
using System.Linq;

namespace TokenSpire2.Solver;

/// <summary>
/// Random tiebreaker for scored options.
/// When multiple options have equal (or very close) scores, picks one randomly
/// instead of always picking the first — avoiding deterministic bias.
/// </summary>
public static class Tiebreaker
{
    /// <summary>
    /// 2% relative or 3.0 absolute, whichever is larger.
    /// Scores within this range of the best score are treated as tied.
    /// </summary>
    private const double RELATIVE_EPSILON = 0.02;
    private const double ABSOLUTE_EPSILON = 3.0;

    // ═══════════════════════════════════════════════════════════════════════
    // MULTIPLAYER DETERMINISM: Do NOT use System.Random for tiebreaking.
    // Two game instances have different Random seeds, so _rng.Next()
    // produces different picks → game state diverges → StateDivergence
    // disconnect. Instead, always pick the first tied element. Scores
    // within epsilon of each other are functionally equivalent, so any
    // deterministic choice is correct. (If varied picks are desired later,
    // use FNV‑1a hash of item identities, not System.Random.)
    // ═══════════════════════════════════════════════════════════════════════

    // ── Core: pick from an ALREADY-SORTED list (descending by score) ──────

    /// <summary>
    /// From a list already sorted descending by score, pick the best element.
    /// If multiple elements have scores within epsilon of the top score,
    /// deterministically select the first (scores are tied — all equivalent).
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

        // Deterministic: always pick first among tied. All scores within
        // epsilon are functionally equivalent. Random (System.Random)
        // would break multiplayer lockstep — each instance has a different
        // seed, producing different picks and StateDivergence disconnects.
        return tied[0];
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

        // Deterministic: always pick first among tied items.
        return tied[0].item;
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

        // Deterministic: always pick first among tied items.
        return tied[0];
    }
}
