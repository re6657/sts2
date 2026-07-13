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

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly string _personaPrompt;
    private readonly List<string> _recentMessages = new(); // prevent short-term repeats

    // ── Shared system prompt (Part A) ──────────────────────────────
    private const string SharedPrompt = @"
你正在和玩家一起玩《杀戮尖塔2》多人合作模式。根据当前游戏局势，用角色的语气写6~8句短对话。

格式要求（严格遵守）：
- 每句独立一行，用换行分隔
- 每句不超过15个中文字符
- 不要编号，不要加前缀（如 1. 2. - 等）
- 每句都应该是独立的一句台词
- 符合角色的说话风格和口癖
- 可以：评论战局、给建议、吐槽、鼓励、和其他Bot互动
- 不要：重复之前说过的话、说『作为AI』之类的话

输出示例（注意：每行一句，无编号，15字以内）：
人类小心！
这一刀好疼啊……
快用防御呀
哼，让我来
好机会呢~
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
    }

    /// <summary>
    /// Send a chat request to DeepSeek API. Returns 6-8 short lines of dialogue,
    /// or null on failure. Each line is ≤15 Chinese characters.
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
