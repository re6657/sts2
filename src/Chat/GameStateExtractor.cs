using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace TokenSpire2.Chat;

/// <summary>
/// Extracts current game state as a narrative Chinese description
/// for the AI chat system.
///
/// Characters are CASUAL OBSERVERS who don't understand game mechanics,
/// so the context uses qualitative, vibe-based descriptions instead of
/// exact numbers and card names.
///
/// Format:
/// 【当前局势】
/// 玩家选的角色是铁甲战士。血量还算健康，手上捏着几张牌。
/// 对面有两个怪物，看起来来势汹汹。
/// 已经打了好几回合了。
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
    /// Build a narrative Chinese context string describing current game state.
    /// Uses qualitative descriptions — characters are casual observers,
    /// not expert players analyzing mechanics.
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
            sb.AppendLine("【当前局势——你是外行，只能看懂大概】");

            // ── Character ─────────────────────────────────────────────
            var charName = GetCharacterName(player);
            sb.Append($"玩家选的角色是{charName}。");

            // ── HP status — qualitative, not exact numbers ────────────
            var hp = player.Creature.CurrentHp;
            var maxHp = player.Creature.MaxHp;
            var hpRatio = (double)hp / maxHp;

            if (hpRatio > 0.8)
                sb.Append("血量很健康，看起来游刃有余。");
            else if (hpRatio > 0.5)
                sb.Append("血量还行，受了点伤但不严重。");
            else if (hpRatio > 0.25)
                sb.Append("血量不太乐观，好像被打得挺惨的……");
            else
                sb.Append("血量见底了！感觉随时可能输掉。");

            // ── Block — simple mention if noticeable ──────────────────
            var block = player.Creature.Block;
            if (block > 10)
                sb.Append("身上套着一层厚厚的护盾。");
            else if (block > 0)
                sb.Append("有一点护盾。");

            sb.AppendLine();

            // ── Hand — vague, no card names ───────────────────────────
            try
            {
                var hand = PileType.Hand.GetPile(player).Cards.ToList();
                if (hand.Count > 7)
                    sb.AppendLine("手上捏着一大把牌。");
                else if (hand.Count > 3)
                    sb.AppendLine($"手上大概有{hand.Count}张牌。");
                else if (hand.Count > 0)
                    sb.AppendLine("手上没几张牌了。");
                else
                    sb.AppendLine("手上空空如也。");
            }
            catch { sb.AppendLine("手上捏着几张牌。"); }

            // ── Enemies — qualitative, no mechanics ───────────────────
            try
            {
                var combatState = cm.DebugOnlyGetState();
                var enemies = combatState?.Enemies?.Where(e => e.IsAlive).ToList();
                if (enemies != null && enemies.Count > 0)
                {
                    sb.Append("对面有");
                    if (enemies.Count == 1)
                    {
                        var name = enemies[0].Monster?.Id?.Entry ?? "一个怪物";
                        var eHpRatio = enemies[0].MaxHp > 0
                            ? (double)enemies[0].CurrentHp / enemies[0].MaxHp
                            : 0.5;
                        sb.Append($"{enemies.Count}个敌人（{name}）");
                        if (eHpRatio < 0.3)
                            sb.Append("，已经快被打死了");
                        else if (eHpRatio > 0.8)
                            sb.Append("，看起来还精神得很");
                        sb.Append("。");
                    }
                    else
                    {
                        sb.Append($"{enemies.Count}个敌人");
                        // Give a rough feel for the group
                        var avgHpRatio = enemies.Average(e =>
                            e.MaxHp > 0 ? (double)e.CurrentHp / e.MaxHp : 0.5);
                        if (avgHpRatio > 0.8)
                            sb.Append("，个个精神抖擞。");
                        else if (avgHpRatio < 0.3)
                            sb.Append("，都已经被打得差不多了。");
                        else
                            sb.Append("。");
                    }

                    // Mention if any enemy looks particularly dangerous
                    var biggest = enemies.OrderByDescending(e => e.MaxHp).FirstOrDefault();
                    if (biggest != null && biggest.MaxHp > 80)
                    {
                        var bigName = biggest.Monster?.Id?.Entry ?? "有个大块头";
                        sb.Append($"其中{bigName}看起来特别大只，感觉不好惹。");
                    }

                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine("对面好像没有敌人了？（可能快结束了）");
                }
            }
            catch { sb.AppendLine("对面有几个怪物，看不太清楚。"); }

            // ── Turn number — vague ────────────────────────────────────
            var turnNum = player.PlayerCombatState?.TurnNumber ?? 0;
            if (turnNum > 10)
                sb.Append("已经打了好久好久了……");
            else if (turnNum > 5)
                sb.Append("打了好几回合了。");
            else if (turnNum > 2)
                sb.Append($"这是第{turnNum}回合。");
            else if (turnNum > 0)
                sb.Append("才刚开始。");
            sb.AppendLine();

            // ── HP warning ────────────────────────────────────────────
            if (hpRatio < 0.3)
                sb.AppendLine("⚠️ 看起来非常危险！随时可能输掉。");
            else if (hpRatio < 0.5)
                sb.AppendLine("⚠️ 情况好像不太妙……");

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
}
