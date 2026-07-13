using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TokenSpire2.Chat;

/// <summary>
/// DeepSeek API client for AI-generated speech bubble text.
///
/// Generates 6-8 short sentences (≤15 Chinese characters each) per request.
/// Each bot instance creates its own ChatEngine with the assigned
/// character persona.
/// </summary>
public class ChatEngine
{
    private static readonly HttpClient _http = new();
    private static ChatEngine? _instance;

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly string _personaPrompt;
    private readonly List<string> _recentMessages = new(); // prevent short-term repeats

    /// <summary>
    /// Returns the most recently created ChatEngine instance, or null.
    /// Used by CombatRecorder to call the API outside of AutoSlayNode.
    /// </summary>
    public static ChatEngine? GetInstance() => _instance;

    // ── Shared system prompt (Part A) ──────────────────────────────
    // This prompt defines HOW the character behaves in the game context.
    // It overrides the "life companion" default and forces "gaming buddy" mode.
    private const string SharedPrompt = @"
你正在看玩家玩《杀戮尖塔2》，坐在旁边偶尔说说话——不是每回合都必须开口。

【最核心的三条规则】
1. 沉默是默认状态。不是每回合都要说话。大部分时候你只是在看。一场战斗输出1~4句就够了。
2. 开口是因为「想说」，不是「该说」。看到真正值得评论的事才说话——而不是因为系统触发了你。
3. 你是游戏搭子，不是女朋友/监护人。不需要每条消息都鼓励。可以吐槽、沉默、给错建议后嘴硬、或者根本不理玩家。

【游戏搭子的自然行为】
- 大部分回合不说话——盯着屏幕看牌，隔好几回合才冒一句
- 句子短、碎，不完整是常态：「嗯。」「行。」「打左边。」「随便。」
- 情绪要有起伏：打输了叹气、Boss太强一起骂、抽到好牌兴奋——不是每条消息都温柔
- 给的建议可能是错的：「打痛击！」然后下一句「……算了，刚才应该先防御。」
- 不一定要有用：「不知道」「看你自己」「对面好恶心」都是合法消息
- 可以吐槽玩家：「……刚才那牌不该拿的。」但不是骂人，是损友互损
- 玩家HP低时可以紧张，但不要每次都说——沉默盯着屏幕比「小心哦」更真实
- 赢了不需要喝彩——「收工。」「下一把？」比「太棒了！」更自然
- 输了不需要安慰——「……啧。」「再来一局？」就够了
- 允许连续几回合不说话，然后突然冒一句完全跑题的：「有点渴了。」

【语气铁律——游戏聊天没那么精致】
- 大部分时候不需要语气词——不是每条消息都带「~」「哦」「呢」「呀」
- 可以用游戏用语：「寄」「贪了」「翻车」「还行」
- 不要说情话和甜腻的关心——打Boss时说「我会一直陪着你的哦~」非常不合时宜
- 用直接建议代替反问撒娇——说「先防御」而不是「要不要先防御呢？」

【格式要求】
- 输出1~4行（够了，不需要更多）
- 如果没什么好说的，输出1行甚至空行都可以
- 每句不超过15个中文字符
- 不要编号，不要前缀
- 句子可以不完整

【输出示例】
那只胖的要逃了。
先打它。
……不是，我说了先打它啊。

（几回合沉默后）

……还行。
Boss好恶心。

【绝对禁止】
- 不要每条消息都鼓励——沉默比无脑「加油」更真实
- 不要每条消息都带「~」「哦」「呢」「呀」——游戏聊天没那么多语气词
- 不要在战斗中甜腻——不要说「我会一直陪着你」「你好棒哦」
- 不要把每句话说完整——碎片是正常的
- 不要用反问句给游戏建议
";

    public ChatEngine(string personaPrompt, string? apiKey = null, string? model = null, string? baseUrl = null)
    {
        _personaPrompt = personaPrompt;

        if (apiKey != null)
            _apiKey = apiKey;
        else if (AiChatConfig.IsInitialized)
            _apiKey = AiChatConfig.Instance.ApiKey;
        else
            _apiKey = "";

        _model = model ?? (AiChatConfig.IsInitialized ? AiChatConfig.Instance.Model : "deepseek-chat");
        _baseUrl = baseUrl ?? (AiChatConfig.IsInitialized ? AiChatConfig.Instance.BaseUrl : "https://api.deepseek.com/v1");

        _instance = this;
    }

    /// <summary>
    /// Send a chat request to DeepSeek API. Returns 1-4 short lines of dialogue,
    /// or null on failure. Each line is ≤15 Chinese characters.
    /// Silence is expected — the prompt encourages the AI to stay quiet unless
    /// it genuinely has something to say.
    /// </summary>
    public async Task<string[]?> SendAsync(string gameStateContext)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            MainFile.Logger?.Info("[ChatEngine] No API key configured");
            return null;
        }

        if (string.IsNullOrEmpty(gameStateContext))
        {
            MainFile.Logger?.Info("[ChatEngine] Empty game state — skipping");
            return null;
        }

        try
        {
            var fullSystemPrompt = _personaPrompt + "\n\n" + SharedPrompt;

            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = fullSystemPrompt },
                    new { role = "user", content = gameStateContext }
                },
                max_tokens = 200,
                temperature = AiChatConfig.IsInitialized ? AiChatConfig.Instance.Temperature : 0.9,
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            request.Content = content;

            // 10-second timeout for multi-line response
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                MainFile.Logger?.Info($"[ChatEngine] API error {response.StatusCode}: {errorBody[..Math.Min(errorBody.Length, 200)]}");
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseJson);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0)
                return null;

            var message = choices[0].GetProperty("message");
            var text = message.GetProperty("content").GetString();

            if (string.IsNullOrWhiteSpace(text))
                return null;

            // Parse multi-line response into individual lines
            var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<string>();

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                // Strip leading numbers/separators: "1." "2." "- " "1、" etc.
                while (line.Length > 0 && (char.IsDigit(line[0]) || line[0] == '.' || line[0] == '-' || line[0] == '、' || line[0] == '）' || line[0] == ')'))
                    line = line[1..].Trim();
                // Strip common prefixes
                if (line.StartsWith("说：")) line = line[2..];
                if (line.StartsWith(": ")) line = line[2..];

                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length < 2) continue; // too short

                // Trim to ≤15 chars (game speech bubble limit)
                if (line.Length > 15)
                    line = line[..15];

                // Skip near-duplicates
                bool isDup = false;
                foreach (var recent in _recentMessages)
                {
                    if (ComputeSimilarity(line, recent) > 0.7)
                    {
                        isDup = true;
                        break;
                    }
                }
                if (isDup) continue;

                result.Add(line);
                _recentMessages.Add(line);
                if (_recentMessages.Count > 20)
                    _recentMessages.RemoveAt(0);
            }

            if (result.Count == 0)
                return null;

            MainFile.Logger?.Info($"[ChatEngine] Generated {result.Count} lines: {string.Join(" | ", result)}");
            return result.ToArray();
        }
        catch (TaskCanceledException)
        {
            MainFile.Logger?.Info("[ChatEngine] Request timed out");
            return null;
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Info($"[ChatEngine] Error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Generate dialogue REACTING to a combat that just ended.
    /// Uses a different system prompt focused on post-combat reflection.
    /// The generated lines are stored for delivery at the start of the
    /// NEXT combat, so they read as natural post-battle commentary.
    ///
    /// Returns 4-6 short lines, or null on failure.
    /// </summary>
    public async Task<string[]?> SendPostCombatAsync(string combatSummary)
    {
        if (string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(combatSummary))
            return null;

        try
        {
            const string postCombatPrompt = @"
你刚看完一场《杀戮尖塔2》的战斗。用角色的语气写2~4句战斗结束后的反应。战斗已经结束，你在复盘/吐槽/松一口气。

格式要求：
- 每句独立一行
- 每句不超过15个中文字符
- 不要编号，不要前缀
- 内容：评价刚才的战斗、吐槽怪物、庆幸过关、或者简单说「下一把」
- 不需要每场都喝彩，也不需要每场都安慰
- 赢了可以只说「收工。」，输了可以只说「……啧。再来。」
- 语气是游戏搭子，不是女朋友——不需要甜腻的祝贺或肉麻的安慰
- 不要：说『作为AI』之类的话

示例：
赢了：
收工。
那个精英怪真离谱……
下一把？

输了：
……啧。
刚才应该拿那张牌的。
再来一局。
";

            var fullSystemPrompt = _personaPrompt + "\n\n" + postCombatPrompt;

            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = fullSystemPrompt },
                    new { role = "user", content = combatSummary }
                },
                max_tokens = 200,
                temperature = AiChatConfig.IsInitialized ? AiChatConfig.Instance.Temperature : 0.9,
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            request.Content = content;

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseJson);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0)
                return null;

            var message = choices[0].GetProperty("message");
            var text = message.GetProperty("content").GetString();

            if (string.IsNullOrWhiteSpace(text))
                return null;

            // Parse lines (same logic as SendAsync)
            var lines = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<string>();
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                while (line.Length > 0 && (char.IsDigit(line[0]) || line[0] == '.' || line[0] == '-' || line[0] == '、' || line[0] == '）' || line[0] == ')'))
                    line = line[1..].Trim();
                if (line.StartsWith("说：")) line = line[2..];
                if (line.StartsWith(": ")) line = line[2..];
                if (string.IsNullOrWhiteSpace(line) || line.Length < 2) continue;
                if (line.Length > 15) line = line[..15];
                result.Add(line);
            }

            if (result.Count == 0) return null;

            MainFile.Logger?.Info($"[ChatEngine] Post-combat: {result.Count} lines — {string.Join(" | ", result)}");
            return result.ToArray();
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Info($"[ChatEngine] Post-combat error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Simple character-level similarity for duplicate detection.
    /// </summary>
    private static double ComputeSimilarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        int matches = 0;
        int maxLen = Math.Max(a.Length, b.Length);
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] == b[i]) matches++;
        return (double)matches / maxLen;
    }
}
