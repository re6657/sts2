using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Runs;

namespace TokenSpire2.Chat;

/// <summary>
/// Extracts current combat state as a compact Chinese string
/// for the AI chat system.
///
/// Format:
/// 【当前局势】
/// 角色：铁甲战士 | HP：68/80 | 格挡：12 | 能量：3/3
/// 手牌：痛击(2费) 打击(1费) 防御(1费)
/// 敌人：大颚虫 HP 44/48 意图：攻击12
/// 回合：第3回合 | 力量：0 | 敏捷：0
/// </summary>
public static class GameStateExtractor
{
    private static readonly Dictionary<string, string> CharacterNames = new()
    {
        ["IRONCLAD"] = "铁甲战士",
        ["SILENT"] = "静默猎人",
        ["DEFECT"] = "故障机器人",
        ["REGENT"] = "君王",
        ["NECROBINDER"] = "死灵法师",
    };

    /// <summary>
    /// Build a compact Chinese context string describing current combat state.
    /// Returns empty string if not in combat or state is unavailable.
    /// </summary>
    public static string BuildContext()
    {
        try
        {
            var cm = CombatManager.Instance;
            if (cm == null || !cm.IsInProgress)
                return "";

            var rs = RunManager.Instance?.DebugOnlyGetState();
            if (rs == null) return "";

            var player = LocalContext.GetMe(rs);
            if (player == null) return "";

            var sb = new StringBuilder();
            sb.AppendLine("【当前局势】");

            // ── Player state ──────────────────────────────────────────
            var charName = GetCharacterName(player);
            var hp = player.Creature.CurrentHp;
            var maxHp = player.Creature.MaxHp;
            var block = player.Creature.Block;
            var energy = player.PlayerCombatState?.Energy ?? 0;
            var maxEnergy = player.MaxEnergy;

            sb.Append($"角色：{charName} | HP：{hp}/{maxHp} | 格挡：{block} | 能量：{energy}/{maxEnergy}");

            // Stars (Necrobinder)
            var stars = player.PlayerCombatState?.Stars ?? 0;
            if (stars > 0)
                sb.Append($" | 星星：{stars}");

            sb.AppendLine();

            // ── Hand cards ────────────────────────────────────────────
            try
            {
                var hand = PileType.Hand.GetPile(player).Cards.ToList();
                if (hand.Count > 0)
                {
                    sb.Append("手牌：");
                    foreach (var card in hand)
                    {
                        var cardName = GetCardName(card);
                        var cost = GetCardCost(card);
                        sb.Append($"{cardName}({cost}) ");
                    }
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine("手牌：无");
                }
            }
            catch { sb.AppendLine("手牌：—"); }

            // ── Enemies ───────────────────────────────────────────────
            try
            {
                var combatState = cm.DebugOnlyGetState();
                var enemies = combatState?.Enemies?.Where(e => e.IsAlive).ToList();
                if (enemies != null && enemies.Count > 0)
                {
                    sb.Append("敌人：");
                    foreach (var enemy in enemies)
                    {
                        var enemyName = enemy.Monster?.Id?.Entry ?? "未知敌人";
                        var eHp = enemy.CurrentHp;
                        var eMaxHp = enemy.MaxHp;
                        var eBlock = enemy.Block;

                        sb.Append($"{enemyName} HP {eHp}/{eMaxHp}");
                        if (eBlock > 0)
                            sb.Append($" 格挡{eBlock}");

                        // Intent
                        var intent = enemy.Monster?.NextMove?.Intents?.FirstOrDefault();
                        if (intent != null)
                        {
                            var intentType = intent.IntentType.ToString();
                            sb.Append($" 意图：{intentType}");
                        }

                        sb.Append(" | ");
                    }
                    // Remove trailing " | "
                    if (sb.Length >= 3)
                        sb.Length -= 3;
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine("敌人：无");
                }
            }
            catch { sb.AppendLine("敌人：—"); }

            // ── Turn info ─────────────────────────────────────────────
            var turnNum = player.PlayerCombatState?.TurnNumber ?? 0;
            sb.Append($"回合：第{turnNum}回合");

            // Powers (strength, dexterity, vulnerable, weak)
            try
            {
                int str = SumPower(player, "Strength");
                int dex = SumPower(player, "Dexterity");
                int vuln = SumPower(player, "Vulnerable");
                int weak = SumPower(player, "Weak");
                int frail = SumPower(player, "Frail");

                if (str != 0) sb.Append($" | 力量：{str}");
                if (dex != 0) sb.Append($" | 敏捷：{dex}");
                if (vuln > 0) sb.Append($" | 易伤：{vuln}");
                if (weak > 0) sb.Append($" | 虚弱：{weak}");
                if (frail > 0) sb.Append($" | 脆弱：{frail}");
            }
            catch { }

            sb.AppendLine();

            // ── Relics ─────────────────────────────────────────────────
            try
            {
                var relics = player.Relics?.ToList();
                if (relics != null && relics.Count > 0)
                {
                    var relicNames = relics.Select(r => r.Id?.Entry ?? "?").Take(8);
                    sb.AppendLine($"遗物：{string.Join(" ", relicNames)}");
                }
            }
            catch { }

            // ── HP ratio note ─────────────────────────────────────────
            var hpRatio = (double)hp / maxHp;
            if (hpRatio < 0.3)
                sb.AppendLine("⚠️ 血量危险！");
            else if (hpRatio < 0.5)
                sb.AppendLine("⚠️ 血量偏低");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Info($"[GameState] Error building context: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Public static helper so other classes (CombatRecorder) can get
    /// character display names without duplicating the mapping.
    /// </summary>
    public static string GetCharacterNameStatic(Player player)
    {
        var id = player.Character?.Id?.Entry ?? "";
        return CharacterNames.GetValueOrDefault(id, id);
    }

    private static string GetCharacterName(Player player)
    {
        return GetCharacterNameStatic(player);
    }

    // Helpers use dynamic/var to avoid type visibility issues with publicized assemblies
    private static string GetCardName(object card)
    {
        try
        {
            var cardType = card.GetType();
            var titleProp = cardType.GetProperty("TitleLocString");
            if (titleProp != null)
            {
                var titleLoc = titleProp.GetValue(card);
                var formatMethod = titleLoc?.GetType().GetMethod("GetFormattedText");
                if (formatMethod != null)
                {
                    var name = formatMethod.Invoke(titleLoc, null) as string;
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
            var idProp = cardType.GetProperty("Id");
            var idVal = idProp?.GetValue(card);
            var entryProp = idVal?.GetType().GetProperty("Entry");
            return entryProp?.GetValue(idVal) as string ?? "?";
        }
        catch { return "?"; }
    }

    private static string GetCardCost(object card)
    {
        try
        {
            var cardType = card.GetType();
            var ecProp = cardType.GetProperty("EnergyCost");
            if (ecProp == null) return "?费";
            var ec = ecProp.GetValue(card);
            var ecType = ec?.GetType();

            var costsX = ecType?.GetProperty("CostsX")?.GetValue(ec);
            if (costsX is true)
                return "X费";

            var resolved = ecType?.GetMethod("GetResolved")?.Invoke(ec, null);
            return $"{resolved}费";
        }
        catch { return "?费"; }
    }

    private static int SumPower(Player player, string powerName)
    {
        // M32: use exact power type name match to avoid substring false-matches
        // (e.g. "Strength" matching "StrengthDrainPower")
        try
        {
            string targetType = powerName + "Power";
            return player.Creature.Powers
                .Where(p => p.GetType().Name.Equals(targetType, StringComparison.OrdinalIgnoreCase))
                .Sum(p => p.Amount);
        }
        catch { return 0; }
    }
}
