using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace TokenSpire2.Solver;

/// <summary>
/// Map path planner — plans the entire route at game start and follows it strictly.
/// Priority order:
///   1. Most campfires (rest sites)
///   2. Fewest elites
///   3. Most shops
///   4. Fewest monsters (normal combats)
/// Path is cached on first plan and never re-planned mid-act.
/// </summary>
public static class MapDecider
{
    /// <summary>
    /// Set by AutoSlayNode before Decide() is called. In multiplayer mode,
    /// all players must select before the map advances, so the bot being
    /// "still on the same row" after clicking is NORMAL — not a rejection.
    /// </summary>
    public static bool InMultiplayerRun;

    private static int _lastClickedRow = -1;
    private static int _lastClickedCol = -1;
    private static double _lastClickTime;
    private static List<NMapPoint>? _cachedPath;
    private static int _cachedPathIndex = -1;
    private static HashSet<(int row, int col)>? _clickedNodes;

    public static void Reset()
    {
        _lastClickedRow = -1;
        _lastClickedCol = -1;
        _lastClickTime = 0;
        _cachedPath = null;
        _cachedPathIndex = -1;
        _clickedNodes = null;
    }

    public static bool Decide(RunState state)
    {
        var mapScreen = NMapScreen.Instance;
        if (mapScreen?.IsOpen != true) return false;

        var allPoints = AutoSlayHelpers.FindAll<NMapPoint>(mapScreen);
        if (allPoints.Count == 0)
        {
            MainFile.Logger.Error("[MapDecider] FindAll<NMapPoint> returned 0 points!");
            return false;
        }

        var enabledPoints = allPoints.Where(p => p.IsEnabled).ToList();

        if (enabledPoints.Count == 0)
        {
            _cachedPath = null;
            _cachedPathIndex = -1;
            return false;
        }

        // ── Single-node fast path ──────────────────────────────────────
        if (enabledPoints.Count == 1)
        {
            var only = enabledPoints[0];
            _clickedNodes ??= new HashSet<(int, int)>();
            int r = only.Point.coord.row;
            int c = only.Point.coord.col;

            if (_clickedNodes.Contains((r, c)))
            {
                // In multiplayer, we're waiting for other players to select —
                // NOT stuck. Don't replan, don't clear cache. Just wait.
                if (InMultiplayerRun)
                    return false;

                double waited = _lastClickTime > 0
                    ? (Godot.Time.GetTicksMsec() / 1000.0) - _lastClickTime : 0;
                if (waited > 5.0)
                {
                    MainFile.Logger.Error($"[MapDecider] Single-node stuck at ({r},{c}) for {waited:F0}s — forcing replan");
                    _clickedNodes.Clear();
                    _cachedPath = null;
                    _cachedPathIndex = -1;
                    return false;
                }
                return false;
            }

            _clickedNodes.Add((r, c));
            MainFile.Logger.Info($"[MapDecider] Single-node: clicking ({r},{c}) {NodeTypeName(only)}");
            _lastClickedRow = r;
            _lastClickedCol = c;
            _lastClickTime = Godot.Time.GetTicksMsec() / 1000.0;
            mapScreen.OnMapPointSelectedLocally(only);
            // B5: Track for click verification
            _clickVerifyRow = r;
            _clickVerifyCol = c;
            _clickVerifyTime = _lastClickTime;

            return true;
        }

        // ── B5: Click verification — check if previous click was rejected ──
        // In multiplayer, all players must select before the map advances,
        // so staying on the same row is NORMAL — NOT a click rejection.
        // Skipping retry avoids an infinite loop of click→re-plan→click.
        if (_clickVerifyRow >= 0 && !InMultiplayerRun)
        {
            double waited = (Godot.Time.GetTicksMsec() / 1000.0) - _clickVerifyTime;
            if (waited > 0.5 && waited < 5.0)
            {
                bool stillOnSameRow = enabledPoints.Any(p =>
                {
                    try { return p.Point.coord.row == _clickVerifyRow; }
                    catch { return false; }
                });
                if (stillOnSameRow && enabledPoints.Count > 1)
                {
                    _clickedNodes.Remove((_clickVerifyRow, _clickVerifyCol));
                    _clickVerifyRow = -1;
                    _clickVerifyCol = -1;
                    MainFile.Logger.Info($"[MapDecider] Click appears rejected — retrying");
                }
                else if (!stillOnSameRow)
                {
                    _clickVerifyRow = -1;
                    _clickVerifyCol = -1;
                }
            }
            else if (waited >= 5.0)
            {
                _clickVerifyRow = -1;
                _clickVerifyCol = -1;
            }
        }

        // ── Try to continue cached path ────────────────────────────────
        NMapPoint? nextNode = TryContinueCachedPath(allPoints, enabledPoints);

        if (nextNode == null)
        {
            // Cache miss — run full path plan (only happens at game start or if stuck)
            _cachedPath = null;
            _cachedPathIndex = -1;

            MainFile.Logger.Info("[MapDecider] Planning full path (one-time at game start)");

            var bestPath = PlanBestPath(allPoints, state);
            if (bestPath == null || bestPath.Count == 0)
            {
                MainFile.Logger.Error("[MapDecider] PlanBestPath returned null/empty!");
                return false;
            }

            // Log the chosen path
            int camps = bestPath.Count(p => NodeTypeName(p) == "Campfire");
            int elites = bestPath.Count(p => NodeTypeName(p) == "Elite");
            int shops = bestPath.Count(p => NodeTypeName(p) == "Shop");
            int monsters = bestPath.Count(p => NodeTypeName(p) == "Normal");

            var pathDesc = string.Join(" → ", bestPath.Select(p =>
                $"({p.Point.coord.row},{p.Point.coord.col}) {NodeTypeName(p)}"));
            MainFile.Logger.Info($"[MapDecider] Chosen path ({bestPath.Count} nodes, " +
                $"C={camps} E={elites} S={shops} M={monsters}): {pathDesc}");

            _cachedPath = bestPath;
            _cachedPathIndex = 0;
            nextNode = bestPath[0];
        }

        if (nextNode == null) return false;

        // Click verification
        int nr = nextNode.Point.coord.row;
        int nc = nextNode.Point.coord.col;
        _clickedNodes ??= new HashSet<(int, int)>();

        if (_clickedNodes.Contains((nr, nc)))
        {
            // In multiplayer, waiting for other players — not stuck.
            if (InMultiplayerRun)
                return false;

            double waited = _lastClickTime > 0
                ? (Godot.Time.GetTicksMsec() / 1000.0) - _lastClickTime : 0;
            if (waited > 3.0)
            {
                MainFile.Logger.Error($"[MapDecider] Click stuck at ({nr},{nc}) for {waited:F0}s — clearing cache");
                _clickedNodes.Clear();
                _cachedPath = null;
                _cachedPathIndex = -1;
                _lastClickTime = 0;
                return false;
            }
            return false;
        }

        MainFile.Logger.Info($"[MapDecider] Clicking ({nr},{nc}) {NodeTypeName(nextNode)} " +
            $"[{_cachedPathIndex + 1}/{_cachedPath?.Count ?? 0}]");

        _lastClickedRow = nr;
        _lastClickedCol = nc;
        _lastClickTime = Godot.Time.GetTicksMsec() / 1000.0;
        _clickedNodes.Add((nr, nc));
        mapScreen.OnMapPointSelectedLocally(nextNode);

        // B5 fix: After clicking, check after a short delay that the click was accepted.
        // If the same row still has nodes enabled (and we're still on same row),
        // the game may have rejected our click. Mark for re-verification.
        _clickVerifyRow = nr;
        _clickVerifyCol = nc;
        _clickVerifyTime = _lastClickTime;
        _clickWasRejected = false;

        return true;
    }

    // B5: Click verification state
    private static int _clickVerifyRow = -1;
    private static int _clickVerifyCol = -1;
    private static double _clickVerifyTime;
    private static bool _clickWasRejected;

    // ═══════════════════════════════════════════════════════════════════
    // Cached path continuation
    // ═══════════════════════════════════════════════════════════════════

    private static NMapPoint? TryContinueCachedPath(List<NMapPoint> allPoints, List<NMapPoint> enabledPoints)
    {
        if (_cachedPath == null || _cachedPath.Count == 0)
            return null;

        // Quick coord → enabled lookup
        var enabledByCoord = new Dictionary<(int row, int col), NMapPoint>();
        foreach (var p in enabledPoints)
        {
            try
            {
                enabledByCoord[(p.Point.coord.row, p.Point.coord.col)] = p;
            }
            catch (Exception ex) { MainFile.Logger?.Info($"[MapDecider] coord access failed: {ex.Message}"); }
        }

        if (enabledByCoord.Count == 0) return null;

        // Find current position: lowest-row enabled node on cached path
        int currentIdx = -1;
        int lowestRow = int.MaxValue;
        for (int i = 0; i < _cachedPath.Count; i++)
        {
            try
            {
                int r = _cachedPath[i].Point.coord.row;
                int c = _cachedPath[i].Point.coord.col;
                if (enabledByCoord.ContainsKey((r, c)) && r < lowestRow)
                {
                    lowestRow = r;
                    currentIdx = i;
                }
            }
            catch (Exception ex) { MainFile.Logger?.Info($"[MapDecider] cached path iteration failed: {ex.Message}"); }
        }

        if (currentIdx < 0)
        {
            MainFile.Logger.Info("[MapDecider] No cached nodes currently enabled");
            return null;
        }

        // Stuck detection
        if (currentIdx == _cachedPathIndex && _cachedPathIndex >= 0)
        {
            double waited = _lastClickTime > 0 ? (Godot.Time.GetTicksMsec() / 1000.0) - _lastClickTime : 0;
            if (waited > 5.0)
            {
                MainFile.Logger.Error($"[MapDecider] STUCK on cached node {currentIdx} for {waited:F0}s — forcing replan");
                return null;
            }
        }

        // Find next enabled node after current position
        for (int i = currentIdx + 1; i < _cachedPath.Count; i++)
        {
            try
            {
                int r = _cachedPath[i].Point.coord.row;
                int c = _cachedPath[i].Point.coord.col;
                if (enabledByCoord.TryGetValue((r, c), out var ep))
                {
                    if (_clickedNodes != null && _clickedNodes.Contains((r, c)))
                        continue;

                    if (i == _cachedPathIndex)
                        return null; // waiting for transition

                    _cachedPathIndex = i;
                    return ep;
                }
            }
            catch { }
        }

        MainFile.Logger.Info("[MapDecider] No more enabled nodes on cached path (reached end?)");
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Path Planning — priority-based
    // ═══════════════════════════════════════════════════════════════════

    private static List<NMapPoint>? PlanBestPath(List<NMapPoint> allPoints, RunState state)
    {
        // Group by row
        var byRow = new Dictionary<int, List<NMapPoint>>();
        foreach (var p in allPoints)
        {
            try
            {
                int row = p.Point.coord.row;
                if (!byRow.ContainsKey(row)) byRow[row] = new List<NMapPoint>();
                byRow[row].Add(p);
            }
            catch { }
        }

        if (byRow.Count == 0) return null;

        // B3 fix: Use the boss node row as the true top row, not Max(keys).
        // Some maps have branches that extend beyond the boss row, which would
        // cause paths to be prematurely terminated at a non-boss row.
        int topRow = byRow.Keys.Max(); // fallback
        foreach (var p in allPoints)
        {
            try
            {
                string nodeType = NodeTypeName(p);
                if (nodeType == "Boss")
                {
                    topRow = p.Point.coord.row;
                    break;
                }
            }
            catch { }
        }
        // If no explicit boss node found, check the highest row for any boss-typed nodes
        if (topRow == byRow.Keys.Max())
        {
            int maxRow = topRow;
            var topRowPoints = byRow.GetValueOrDefault(maxRow, new List<NMapPoint>());
            bool foundBoss = topRowPoints.Any(p =>
            {
                try { return NodeTypeName(p) == "Boss"; }
                catch { return false; }
            });
            if (!foundBoss && byRow.Count >= 2)
            {
                // Check second-highest row for boss too (edge case)
                var rows = byRow.Keys.OrderByDescending(k => k).ToList();
                if (rows.Count > 1)
                {
                    var secondRowPoints = byRow.GetValueOrDefault(rows[1], new List<NMapPoint>());
                    if (secondRowPoints.Any(p =>
                    {
                        try { return NodeTypeName(p) == "Boss"; }
                        catch { return false; }
                    }))
                    {
                        topRow = rows[1];
                    }
                }
            }
        }

        // Find player's current row (lowest row with enabled nodes)
        int currentRow = -1;
        foreach (var kv in byRow.OrderBy(kv => kv.Key))
        {
            if (kv.Value.Any(p => p.IsEnabled))
            {
                currentRow = kv.Key;
                break;
            }
        }
        if (currentRow < 0) return null;

        MainFile.Logger.Info($"[MapDecider] PlanBestPath: rows {currentRow}→{topRow}, {byRow.Count} rows");

        // Build adjacency from game edges
        var adjacency = BuildAdjacency(allPoints, byRow);

        // Start nodes
        var startNodes = allPoints.Where(p => p.Point.coord.row == currentRow && p.IsEnabled).ToList();
        if (startNodes.Count == 0) return null;

        // DFS — enumerate all paths from each start to the top row
        var allPaths = new List<List<NMapPoint>>();
        foreach (var start in startNodes)
        {
            var path = new List<NMapPoint> { start };
            EnumeratePaths(start, currentRow, topRow, adjacency, path, allPaths);
        }

        MainFile.Logger.Info($"[MapDecider] Enumerated {allPaths.Count} paths from {startNodes.Count} starts");

        if (allPaths.Count == 0) return null;

        // ── PRIORITY SORTING ──────────────────────────────────────────
        // 1. Most campfires    (descending)
        // 2. Fewest elites     (ascending, context-adjusted)
        // 3. Most shops        (descending)
        // 4. Fewest monsters   (ascending)
        //
        // Elite count is adjusted by player context:
        //   - Strength scaling → elites are easier, reduced penalty
        //   - Multiple powers → better scaling vs elites
        //   - Frontload (6+ attacks) → can burst elites down
        //   - High HP (>70%) → can afford elite chip damage
        var ns = SolverParams.Instance.Map.NodeScores;
        double elitePenaltyPerNode = Math.Abs(ns.EliteBase); // ~178.7 base
        if (state.HasStrengthScaling) elitePenaltyPerNode -= ns.EliteStrengthModifier;
        if (state.PowerCount >= 2) elitePenaltyPerNode -= ns.ElitePowerModifier;
        if (state.AttackCount >= 6) elitePenaltyPerNode -= ns.EliteFrontloadModifier;
        if (state.HpRatio > 0.7) elitePenaltyPerNode -= ns.EliteHighHpModifier;
        elitePenaltyPerNode = Math.Max(10, elitePenaltyPerNode); // floor

        var sorted = allPaths
            .Select(path =>
            {
                int campfires = path.Count(p => NodeTypeName(p) == "Campfire");
                int elites = path.Count(p => NodeTypeName(p) == "Elite");
                int shops = path.Count(p => NodeTypeName(p) == "Shop");
                int monsters = path.Count(p => NodeTypeName(p) == "Normal");
                // Composite score: higher = better path
                double score = campfires * 10.0 - elites * elitePenaltyPerNode + shops * 3.0 - monsters * 1.0;
                return (path, campfires, elites, shops, monsters, score);
            })
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.campfires)
            .ThenBy(x => x.elites)
            .ThenByDescending(x => x.shops)
            .ThenBy(x => x.monsters)
            .ToList();

        // Log top paths for debugging
        int show = Math.Min(5, sorted.Count);
        MainFile.Logger.Info($"[MapDecider] Elite penalty/ea: {elitePenaltyPerNode:F0} " +
            $"(str={state.HasStrengthScaling} pow={state.PowerCount >= 2} front={state.AttackCount >= 6} hp={state.HpRatio > 0.7})");
        for (int i = 0; i < show; i++)
        {
            var s = sorted[i];
            var types = string.Join(",", s.path.Select(p => NodeTypeName(p)));
            MainFile.Logger.Info($"[MapDecider]   #{i + 1}: score={s.score:F0} C={s.campfires} E={s.elites} S={s.shops} M={s.monsters} [{types}]");
        }

        return sorted[0].path;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Adjacency — reads real edges from game data
    // ═══════════════════════════════════════════════════════════════════

    private static bool _dumpedPointStructure;

    private static Dictionary<NMapPoint, List<NMapPoint>> BuildAdjacency(
        List<NMapPoint> allPoints, Dictionary<int, List<NMapPoint>> byRow)
    {
        var adj = new Dictionary<NMapPoint, List<NMapPoint>>();
        bool explicitFound = false;
        int fallbackCount = 0;

        // One-time dump of point structure to help debug reflection failures
        if (!_dumpedPointStructure && allPoints.Count > 0)
        {
            _dumpedPointStructure = true;
            DumpPointStructure(allPoints[0]);
        }

        foreach (var point in allPoints)
        {
            var neighbors = new List<NMapPoint>();
            int row = point.Point.coord.row;
            int col = point.Point.coord.col;

            var explicitEdges = GetConnectedPoints(point, allPoints);
            if (explicitEdges.Count > 0)
            {
                neighbors = explicitEdges;
                explicitFound = true;
            }
            else
            {
                fallbackCount++;
                // Fallback: connect to next row within column range ±3
                // ⚠️ This creates edges between nodes that may NOT be connected
                // on the real map. Only used when reflection fails to find edges.
                if (byRow.TryGetValue(row + 1, out var nextRow))
                {
                    neighbors = nextRow
                        .Where(p => Math.Abs(p.Point.coord.col - col) <= 3)
                        .ToList();
                }
            }

            adj[point] = neighbors;
        }

        if (explicitFound)
            MainFile.Logger.Info("[MapDecider] BuildAdjacency: EXPLICIT edges from game data");
        else
        {
            MainFile.Logger.Info($"[MapDecider] BuildAdjacency: FALLBACK column-proximity (±3) — {fallbackCount}/{allPoints.Count} nodes used fallback!");
            if (fallbackCount > 0)
                MainFile.Logger.Info("[MapDecider] ⚠ Phantom edges may exist! Check the point structure dump above for actual property names.");
        }

        int totalEdges = adj.Values.Sum(v => v.Count);
        MainFile.Logger.Info($"[MapDecider] BuildAdjacency: {adj.Count} nodes, {totalEdges} edges");
        return adj;
    }

    /// <summary>One-time dump of NMapPoint/Point properties to debug edge detection.</summary>
    private static void DumpPointStructure(NMapPoint point)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[MapDecider] === Point Structure Dump ===");
            sb.AppendLine($"NMapPoint type: {point.GetType().FullName}");

            // Dump NMapPoint properties
            var nmapProps = point.GetType().GetProperties(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            foreach (var prop in nmapProps)
            {
                try
                {
                    var val = prop.GetValue(point);
                    string valStr = val == null ? "null" :
                        val is System.Collections.IEnumerable && !(val is string) ? $"[{val.GetType().Name}]" :
                        val.ToString()?.Replace("\n", " ")[..Math.Min(80, val.ToString()?.Length ?? 0)] ?? "?";
                    sb.AppendLine($"  NMapPoint.{prop.Name}: {prop.PropertyType.Name} = {valStr}");
                }
                catch { }
            }

            // Dump Point (MapPoint) properties
            var pt = point.Point;
            if (pt != null)
            {
                sb.AppendLine($"Point type: {pt.GetType().FullName}");
                var ptProps = pt.GetType().GetProperties(
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                foreach (var prop in ptProps)
                {
                    try
                    {
                        var val = prop.GetValue(pt);
                        string valStr = val == null ? "null" :
                            val is System.Collections.IEnumerable && !(val is string) ? $"[{val.GetType().Name}]" :
                            val.ToString()?.Replace("\n", " ")[..Math.Min(80, val.ToString()?.Length ?? 0)] ?? "?";
                        sb.AppendLine($"  Point.{prop.Name}: {prop.PropertyType.Name} = {valStr}");
                    }
                    catch { }
                }
            }

            sb.AppendLine($"[MapDecider] === End Point Structure Dump ===");
            MainFile.Logger.Info(sb.ToString());
        }
        catch (Exception ex) { MainFile.Logger.Error($"[MapDecider] DumpPointStructure failed: {ex.Message}"); }
    }

    private static List<NMapPoint> GetConnectedPoints(NMapPoint point, List<NMapPoint> allPoints)
    {
        var result = new List<NMapPoint>();
        try
        {
            var ptType = point.GetType();
            foreach (var propName in new[] { "Edges", "Connections", "Links", "Neighbors",
                "NextPoints", "Children", "Targets", "Destinations", "ConnectedPoints",
                "Outgoing", "Incoming", "NextNodes", "Forward", "Backward", "Exits", "Transitions" })
            {
                var prop = ptType.GetProperty(propName);
                if (prop == null) continue;
                var val = prop.GetValue(point);
                if (val == null) continue;

                if (val is System.Collections.IEnumerable enumerable && !(val is string))
                {
                    foreach (var item in enumerable)
                    {
                        if (item is NMapPoint mp) { result.Add(mp); continue; }
                        if (item == null) continue;
                        var itemType = item.GetType();
                        foreach (var subProp in new[] { "Target", "Destination", "Node", "Point", "To", "Other" })
                        {
                            var sp = itemType.GetProperty(subProp);
                            if (sp?.GetValue(item) is NMapPoint connected)
                            {
                                result.Add(connected);
                                break;
                            }
                        }
                    }
                }
                else if (val is NMapPoint singlePoint)
                {
                    result.Add(singlePoint);
                }
                if (result.Count > 0) return result;
            }

            // Check Point (MapPoint) data
            var pointData = point.Point;
            if (pointData != null)
            {
                var pdType = pointData.GetType();
                foreach (var propName in new[] { "Children", "Edges", "Connections", "Links", "Neighbors",
                    "Next", "Targets", "Destinations", "AdjacentNodes", "ConnectedNodes",
                    "NextPoints", "ForwardConnections", "OutgoingEdges",
                    "Outgoing", "Incoming", "NextNodes", "Forward", "Backward", "Exits", "Transitions" })
                {
                    var prop = pdType.GetProperty(propName);
                    if (prop == null) continue;
                    var val = prop.GetValue(pointData);
                    if (val is System.Collections.IEnumerable enumerable && !(val is string))
                    {
                        foreach (var item in enumerable)
                        {
                            if (item == null) continue;
                            var itemType = item.GetType();

                            // Reference equality match (for HashSet<MapPoint>)
                            if (itemType.Name == "MapPoint")
                            {
                                var match = allPoints.FirstOrDefault(p => p.Point == item);
                                if (match != null) result.Add(match);
                                if (result.Count > 0) continue;
                            }

                            // Coordinate match
                            var rowProp = itemType.GetProperty("row") ?? itemType.GetProperty("Row");
                            var colProp = itemType.GetProperty("col") ?? itemType.GetProperty("Col");
                            if (rowProp != null && colProp != null)
                            {
                                int tr = Convert.ToInt32(rowProp.GetValue(item));
                                int tc = Convert.ToInt32(colProp.GetValue(item));
                                var match = allPoints.FirstOrDefault(p =>
                                    p.Point.coord.row == tr && p.Point.coord.col == tc);
                                if (match != null) result.Add(match);
                                continue;
                            }

                            // Sub-property match
                            foreach (var subProp in new[] { "Target", "Destination", "Node", "Point", "To" })
                            {
                                var sp = itemType.GetProperty(subProp);
                                var sv = sp?.GetValue(item);
                                if (sv is NMapPoint mp) { result.Add(mp); break; }
                                if (sv == null) continue;
                                var coordType = sv.GetType();
                                var crProp = coordType.GetProperty("row") ?? coordType.GetProperty("Row");
                                var ccProp = coordType.GetProperty("col") ?? coordType.GetProperty("Col");
                                if (crProp != null && ccProp != null)
                                {
                                    int tr = Convert.ToInt32(crProp.GetValue(sv));
                                    int tc = Convert.ToInt32(ccProp.GetValue(sv));
                                    var match = allPoints.FirstOrDefault(p =>
                                        p.Point.coord.row == tr && p.Point.coord.col == tc);
                                    if (match != null) result.Add(match);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // DFS path enumeration
    // ═══════════════════════════════════════════════════════════════════

    private static void EnumeratePaths(
        NMapPoint current, int currentRow, int topRow,
        Dictionary<NMapPoint, List<NMapPoint>> adjacency,
        List<NMapPoint> currentPath, List<List<NMapPoint>> allPaths)
    {
        if (currentRow >= topRow)
        {
            allPaths.Add(new List<NMapPoint>(currentPath));
            return;
        }

        var neighbors = adjacency.GetValueOrDefault(current, new List<NMapPoint>());
        if (neighbors.Count == 0)
        {
            allPaths.Add(new List<NMapPoint>(currentPath));
            return;
        }

        foreach (var next in neighbors)
        {
            currentPath.Add(next);
            EnumeratePaths(next, next.Point.coord.row, topRow, adjacency, currentPath, allPaths);
            currentPath.RemoveAt(currentPath.Count - 1);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Node type detection
    // ═══════════════════════════════════════════════════════════════════

    private static string NodeTypeName(NMapPoint point)
    {
        try
        {
            // Check Point.PointType (definitive source)
            var pd = point.Point;
            if (pd != null)
            {
                var pt = pd.GetType();
                foreach (var pn in new[] { "PointType", "Room", "room", "NodeType", "RoomType",
                    "Type", "MapNodeType", "NodeModel", "RoomModel", "MapRoomModel",
                    "Contents", "NodeContents" })
                {
                    var prop = pt.GetProperty(pn);
                    if (prop == null) continue;
                    var val = prop.GetValue(pd);
                    if (val == null) continue;

                    var vt = val.GetType().Name;
                    var sv = val.ToString() ?? "";

                    if (sv.Contains("Elite") || vt.Contains("Elite")) return "Elite";
                    if (sv.Contains("Boss") || vt.Contains("Boss")) return "Boss";
                    if (sv.Contains("Shop") || sv.Contains("Merchant") || vt.Contains("Shop") || vt.Contains("Merchant")) return "Shop";
                    if (sv.Contains("Camp") || sv.Contains("Rest") || vt.Contains("Camp") || vt.Contains("Rest")) return "Campfire";
                    if (sv.Contains("Treasure") || sv.Contains("Chest") || vt.Contains("Treasure") || vt.Contains("Chest")) return "Treasure";
                    if (sv.Contains("Unknown") || sv.Contains("Mystery") || sv.Contains("Event") ||
                        sv.Contains("Ancient") || vt.Contains("Unknown") || vt.Contains("Mystery") ||
                        vt.Contains("Event") || vt.Contains("Ancient")) return "Unknown";
                    if (sv.Contains("Monster") || sv.Contains("Enemy") || sv.Contains("Combat") ||
                        vt.Contains("Monster") || vt.Contains("Enemy") || vt.Contains("Combat")) return "Normal";
                }
            }

            // Fallback: check NMapPoint type name
            var t = point.GetType().Name;
            if (t.Contains("Elite")) return "Elite";
            if (t.Contains("Boss")) return "Boss";
            if (t.Contains("Shop") || t.Contains("Merchant")) return "Shop";
            if (t.Contains("Campfire") || t.Contains("Rest")) return "Campfire";
            if (t.Contains("Treasure") || t.Contains("Chest")) return "Treasure";
            if (t.Contains("Unknown") || t.Contains("Event")) return "Unknown";
            if (t.Contains("Normal")) return "Normal";
            return t;
        }
        catch { return "?"; }
    }
}
