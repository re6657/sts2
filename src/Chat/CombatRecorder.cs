using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace TokenSpire2.Chat;

/// <summary>
/// Records per-combat statistics and generates post-combat AI dialogue
/// that is pre-cached for the start of the next combat.
///
/// Flow:
///   Combat start → OnCombatStart() — captures initial HP, enemy data
///   During combat → RecordCardPlayed() — tracks card plays
///   Combat end → OnCombatEnd() — builds summary, calls API async,
///                  stores result in PreGeneratedDialogue
///   Next combat start → ConsumePreGeneratedDialogue() — returns
///                  pre-generated lines, clears cache
/// </summary>
public static class CombatRecorder
{
    // ── Combat-start snapshot ────────────────────────────────────────
    private static int _botStartHp;
    private static int _botStartMaxHp;
    private static string _botCharacter = "";
    private static int _enemyTotalMaxHp;
    private static readonly List<string> _enemyNames = new();
    private static readonly List<string> _enemyTraits = new();

    // ── During-combat tracking ───────────────────────────────────────
    private static readonly List<string> _cardsPlayed = new();
    private static int _attackCardsPlayed;
    private static int _skillCardsPlayed;
    private static int _powerCardsPlayed;

    // ── Multiplayer: other players' start HP ─────────────────────────
    private static readonly Dictionary<string, (int hp, int maxHp, string name)> _otherPlayersStart = new();

    // ── Output ───────────────────────────────────────────────────────
    /// <summary>
    /// Pre-generated dialogue lines for the next combat.
    /// Set by OnCombatEnd() after API call completes.
    /// Consumed by TrySendAiChat() at the start of next combat.
    /// </summary>
    private static List<string>? _preGeneratedDialogue;
    public static List<string>? PreGeneratedDialogue
    {
        get => System.Threading.Volatile.Read(ref _preGeneratedDialogue);
        private set => System.Threading.Volatile.Write(ref _preGeneratedDialogue, value);
    }

    private static bool _active;

    /// <summary>
    /// Call at the start of each combat (first frame where IsInProgress becomes true).
    /// Resets tracking and snapshots current state.
    /// </summary>
    public static void OnCombatStart()
    {
        Reset();

        try
        {
            var cm = CombatManager.Instance;
            if (cm == null || !cm.IsInProgress) return;

            var rs = RunManager.Instance?.DebugOnlyGetState();
            if (rs == null) return;

            var player = LocalContext.GetMe(rs);
            if (player == null) return;

            // Snapshot bot HP
            _botStartHp = player.Creature.CurrentHp;
            _botStartMaxHp = player.Creature.MaxHp;
            _botCharacter = GetCharacterName(player);

            // Snapshot enemies
            try
            {
                var combatState = cm.DebugOnlyGetState();
                var enemies = combatState?.Enemies?.Where(e => e.IsAlive).ToList();
                if (enemies != null)
                {
                    foreach (var enemy in enemies)
                    {
                        var name = enemy.Monster?.Id?.Entry ?? "未知敌人";
                        _enemyNames.Add(name);
                        _enemyTotalMaxHp += enemy.MaxHp;
                        _enemyTraits.Add(DescribeEnemy(name));
                    }
                }
            }
            catch { }

            // Snapshot other players in multiplayer
            try
            {
                foreach (var p in rs.Players)
                {
                    if (p == player) continue;
                    var pName = GetDisplayName(p);
                    _otherPlayersStart[pName] = (p.Creature.CurrentHp, p.Creature.MaxHp, GetCharacterName(p));
                }
            }
            catch { }

            _active = true;

            MainFile.Logger?.Info($"[CombatRecorder] Combat start: " +
                $"{_botCharacter} HP={_botStartHp}/{_botStartMaxHp}, " +
                $"enemies={string.Join(",", _enemyNames)} (totalHP={_enemyTotalMaxHp}), " +
                $"otherPlayers={_otherPlayersStart.Count}");
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Info($"[CombatRecorder] OnCombatStart error: {ex.Message}");
        }
    }

    /// <summary>
    /// Call when the bot successfully plays a card during combat.
    /// </summary>
    public static void RecordCardPlayed(string cardId, bool isAttack, bool isSkill, bool isPower)
    {
        if (!_active) return;
        _cardsPlayed.Add(cardId);
        if (isAttack) _attackCardsPlayed++;
        if (isSkill) _skillCardsPlayed++;
        if (isPower) _powerCardsPlayed++;
    }

    /// <summary>
    /// Call at combat end (first frame where IsInProgress becomes false after combat).
    /// Builds combat summary, starts async API call to generate dialogue for next combat.
    /// Fire-and-forget: the async work runs on a background task; any exception is logged.
    /// </summary>
    public static void OnCombatEnd()
    {
        if (!_active) return;
        _active = false;

        try
        {
            var cm = CombatManager.Instance;
            var rs = RunManager.Instance?.DebugOnlyGetState();
            if (rs == null) return;

            var player = LocalContext.GetMe(rs);
            if (player == null) return;

            int endHp = player.Creature.CurrentHp;
            int hpLost = Math.Max(0, _botStartHp - endHp);

            // Build summary
            var summary = BuildSummary(player, rs, endHp, hpLost);
            if (string.IsNullOrEmpty(summary)) return;

            MainFile.Logger?.Info($"[CombatRecorder] Combat end: HP {_botStartHp}→{endHp} (lost {hpLost}), " +
                $"cardsPlayed={_cardsPlayed.Count} (atk={_attackCardsPlayed} skl={_skillCardsPlayed} pow={_powerCardsPlayed})");

            // Generate dialogue for next combat (async, fire-and-forget)
            // Has plenty of time: rewards screen + map navigation = 30+ seconds
            var engine = ChatEngine.GetInstance();
            if (engine != null)
            {
                var summaryCopy = summary;
                _ = OnCombatEndAsync(engine, summaryCopy);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Info($"[CombatRecorder] OnCombatEnd error: {ex.Message}");
        }
    }

    /// <summary>
    /// Async work for post-combat dialogue generation. Fire-and-forget from OnCombatEnd().
    /// All exceptions are caught and logged — this runs on a thread-pool thread after await.
    /// </summary>
    private static async Task OnCombatEndAsync(ChatEngine engine, string summary)
    {
        try
        {
            var lines = await engine.SendPostCombatAsync(summary).ConfigureAwait(false);
            if (lines != null && lines.Length > 0)
            {
                PreGeneratedDialogue = new List<string>(lines);
                MainFile.Logger?.Info($"[CombatRecorder] Pre-generated {lines.Length} lines for next combat: " +
                    $"{string.Join(" | ", lines)}");
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Info($"[CombatRecorder] OnCombatEndAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// Consume and return pre-generated dialogue lines. Returns null if none cached.
    /// Call at the start of next combat.
    /// </summary>
    public static List<string>? ConsumePreGeneratedDialogue()
    {
        var result = PreGeneratedDialogue;
        PreGeneratedDialogue = null;
        return result;
    }

    // ── Private helpers ───────────────────────────────────────────────

    private static void Reset()
    {
        _botStartHp = 0;
        _botStartMaxHp = 0;
        _botCharacter = "";
        _enemyTotalMaxHp = 0;
        _enemyNames.Clear();
        _enemyTraits.Clear();
        _cardsPlayed.Clear();
        _attackCardsPlayed = 0;
        _skillCardsPlayed = 0;
        _powerCardsPlayed = 0;
        _otherPlayersStart.Clear();
        _active = false;
    }

    private static string BuildSummary(Player player, object rs, int endHp, int hpLost)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【战斗结束总结】");

        // ── Enemies defeated ──────────────────────────────────────────
        if (_enemyNames.Count > 0)
        {
            // Group identical enemy names
            var grouped = _enemyNames.GroupBy(n => n)
                .Select(g => g.Count() > 1 ? $"{g.Key}×{g.Count()}" : g.Key);
            sb.Append("刚刚打败了：").Append(string.Join("、", grouped));

            // Add traits for unique enemies
            var uniqueTraits = _enemyTraits.Distinct().Where(t => t.Length > 0).ToList();
            if (uniqueTraits.Count > 0)
                sb.Append(" | 特征：").Append(string.Join("；", uniqueTraits.Take(3)));

            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("战斗类型：未知");
        }

        // ── Bot self-stats ─────────────────────────────────────────────
        sb.Append($"自身({_botCharacter})：HP {endHp}/{_botStartMaxHp}");
        if (hpLost > 0)
            sb.Append($"（掉了{hpLost}血）");
        else
            sb.Append("（无伤）");

        // Cards played summary
        if (_cardsPlayed.Count > 0)
        {
            sb.Append($" | 打出{_cardsPlayed.Count}张牌");
            if (_attackCardsPlayed > 0) sb.Append($"（{_attackCardsPlayed}攻");
            if (_skillCardsPlayed > 0) sb.Append($" {_skillCardsPlayed}技");
            if (_powerCardsPlayed > 0) sb.Append($" {_powerCardsPlayed}能");
            sb.Append("）");

            // Top 3 most played cards
            var topCards = _cardsPlayed.GroupBy(c => c)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => g.Key);
            sb.Append(" | 常用牌：").Append(string.Join("、", topCards));
        }

        // Damage dealt (estimated from total enemy HP / estimated share)
        if (_enemyTotalMaxHp > 0)
        {
            int totalPlayers = 1 + _otherPlayersStart.Count;
            int estimatedDamage = _enemyTotalMaxHp / totalPlayers;
            sb.Append($" | 约造成{estimatedDamage}伤害");
        }

        sb.AppendLine();

        // ── Other players ──────────────────────────────────────────────
        if (_otherPlayersStart.Count > 0)
        {
            try
            {
                var rs2 = RunManager.Instance?.DebugOnlyGetState();
                if (rs2 != null)
                {
                    foreach (var p in rs2.Players)
                    {
                        if (p == player) continue;
                        var pName = GetDisplayName(p);
                        if (!_otherPlayersStart.TryGetValue(pName, out var start)) continue;

                        int pHpLost = Math.Max(0, start.hp - p.Creature.CurrentHp);
                        sb.Append($"队友({pName})：{GetCharacterName(p)} | HP {p.Creature.CurrentHp}/{start.maxHp}");
                        if (pHpLost > 0)
                            sb.Append($"（掉了{pHpLost}血）");
                        else
                            sb.Append("（无伤）");

                        // Estimate other player damage share
                        if (_enemyTotalMaxHp > 0)
                        {
                            int totalPlayers = 1 + _otherPlayersStart.Count;
                            sb.Append($" | 约造成{_enemyTotalMaxHp / totalPlayers}伤害");
                        }
                        sb.AppendLine();
                    }
                }
            }
            catch { }
        }

        // ── Combat evaluation ─────────────────────────────────────────
        int totalHpLost = hpLost + _otherPlayersStart.Sum(kv =>
        {
            try
            {
                var rs3 = RunManager.Instance?.DebugOnlyGetState();
                var otherPlayer = rs3?.Players?.FirstOrDefault(p => GetDisplayName(p) == kv.Key);
                if (otherPlayer != null)
                    return Math.Max(0, kv.Value.hp - otherPlayer.Creature.CurrentHp);
            }
            catch { }
            return 0;
        });

        int maxTotalHp = _botStartMaxHp + _otherPlayersStart.Sum(kv => kv.Value.maxHp);
        double teamHpPercent = maxTotalHp > 0 ? (double)(maxTotalHp - totalHpLost) / maxTotalHp : 1.0;

        if (teamHpPercent > 0.85)
            sb.AppendLine("总体评价：轻松获胜，全员状态良好");
        else if (teamHpPercent > 0.6)
            sb.AppendLine("总体评价：有惊无险，部分队友受伤");
        else if (teamHpPercent > 0.3)
            sb.AppendLine("总体评价：惨胜，多人受伤较重");
        else
            sb.AppendLine("总体评价：险胜！全队濒临死亡");

        return sb.ToString();
    }

    private static string GetCharacterName(Player player)
    {
        return GameStateExtractor.GetCharacterNameStatic(player);
    }

    private static string GetDisplayName(Player player)
    {
        try
        {
            // Use the Steam persona name or a fallback
            var steamName = player.GetType().GetProperty("DisplayName")?.GetValue(player) as string;
            return steamName ?? player.Character?.Id?.Entry ?? "Unknown";
        }
        catch { return "Unknown"; }
    }

    /// <summary>
    /// Return a brief trait description for well-known enemies.
    /// </summary>
    private static string DescribeEnemy(string enemyName)
    {
        return enemyName switch
        {
            "大颚虫" => "攻击随回合增强",
            "小颚虫" => "会防御会攻击",
            "邪教徒" => "会使用仪式增强力量",
            "中颚虫" or "蓝颚虫" or "红颚虫" => "基础敌人",
            "史莱姆王" => "大型史莱姆，会召唤小史莱姆",
            "守护者" => "变形攻防模式切换",
            "六火亡魂" => "多段攻击，攻击力递增",
            "地精大块头" => "高血量，愤怒时攻击增强",
            "地精法师" => "会给队友加buff",
            _ => enemyName.Contains("Boss") || enemyName.Contains("boss") ? "BOSS" : "",
        };
    }
}
