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
    private static readonly Dictionary<string, ChatEngine> _instances = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly string _personaPrompt;
    private readonly string _characterName;
    private readonly string _kaomojiArchetype; // tsundere / gentle / sweet / balanced
    private readonly List<string> _recentMessages = new(); // prevent short-term repeats

    /// <summary>
    /// Returns the most recently created ChatEngine instance, or null.
    /// Deprecated: prefer GetInstanceByCharacter for multi-bot setups.
    /// </summary>
    public static ChatEngine? GetInstance() => _instance;

    /// <summary>Get a ChatEngine by character name, falling back to the most recent instance.</summary>
    public static ChatEngine? GetInstanceByCharacter(string characterName)
    {
        if (!string.IsNullOrEmpty(characterName) && _instances.TryGetValue(characterName, out var engine))
            return engine;
        return _instance;
    }

    // All prompt blocks are now in PromptLibrary.cs — modular, composable,
    // and independently editable. See: src/Chat/PromptLibrary.cs

    public ChatEngine(string personaPrompt, string? apiKey = null, string? model = null, string? baseUrl = null, string? characterName = null)
    {
        _personaPrompt = personaPrompt;
        _characterName = characterName ?? "";
        _kaomojiArchetype = ResolveKaomojiArchetype(_characterName);

        if (apiKey != null)
            _apiKey = apiKey;
        else if (AiChatConfig.IsInitialized)
            _apiKey = AiChatConfig.Instance.ApiKey;
        else
            _apiKey = "";

        _model = model ?? (AiChatConfig.IsInitialized ? AiChatConfig.Instance.Model : "deepseek-chat");
        _baseUrl = baseUrl ?? (AiChatConfig.IsInitialized ? AiChatConfig.Instance.BaseUrl : "https://api.deepseek.com/v1");

        _instance = this;
        if (!string.IsNullOrEmpty(_characterName))
            _instances[_characterName] = this;
    }

    /// <summary>
    /// Get kaomoji archetype from CharacterProfileManager metadata.
    /// Each .md file declares its archetype via: &lt;!-- kaomoji: tsundere --&gt;
    /// New characters work immediately — no code change needed.
    /// Falls back to "balanced" if no metadata found.
    /// </summary>
    private static string ResolveKaomojiArchetype(string characterName)
    {
        return CharacterProfileManager.GetKaomojiArchetype(characterName);
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
            var fullSystemPrompt = PromptLibrary.BuildCombatSystemPrompt(_personaPrompt, _kaomojiArchetype);

            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = fullSystemPrompt },
                    new { role = "user", content = PromptLibrary.WrapCombatContext(gameStateContext) }
                },
                max_tokens = 400,  // pipeline mode: generate 6-8 lines per call
                temperature = AiChatConfig.IsInitialized ? AiChatConfig.Instance.Temperature : 0.9,
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            request.Content = content;

            // 15-second timeout for multi-line pipeline response
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
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
            var fullSystemPrompt = PromptLibrary.BuildPostCombatSystemPrompt(_personaPrompt, _kaomojiArchetype);

            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = fullSystemPrompt },
                    new { role = "user", content = PromptLibrary.WrapPostCombatContext(combatSummary) }
                },
                max_tokens = 300,
                temperature = AiChatConfig.IsInitialized ? AiChatConfig.Instance.Temperature : 0.9,
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            request.Content = content;

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
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
    /// Generate a conversation turn — 1-2 lines that continue a multi-bot
    /// conversation. Includes recent chat history so the AI can respond to
    /// what other characters just said.
    /// </summary>
    /// <param name="gameStateContext">Current game state (same format as SendAsync)</param>
    /// <param name="conversationHistory">Recent messages from all bots, e.g. "德丽莎: 好恶心…"</param>
    /// <param name="myName">This character's display name</param>
    /// <param name="otherNames">Other characters in the conversation</param>
    public async Task<string[]?> SendConversationTurnAsync(
        string gameStateContext,
        string conversationHistory,
        string myName,
        string otherNames)
    {
        if (string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(gameStateContext))
            return null;

        try
        {
            // Build a conversation-aware user message via PromptLibrary
            var userMessage = PromptLibrary.WrapConversationContext(gameStateContext, conversationHistory, myName);

            var fullSystemPrompt = PromptLibrary.BuildConversationSystemPrompt(_personaPrompt, _kaomojiArchetype, otherNames);

            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = fullSystemPrompt },
                    new { role = "user", content = userMessage }
                },
                max_tokens = 300,  // pipeline mode: generate multiple lines per turn
                temperature = AiChatConfig.IsInitialized ? AiChatConfig.Instance.Temperature : 0.9,
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            request.Content = content;

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                MainFile.Logger?.Info($"[ChatEngine] Conv API error {response.StatusCode}: {errorBody[..Math.Min(errorBody.Length, 200)]}");
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseJson);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return null;

            var message = choices[0].GetProperty("message");
            var text = message.GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(text)) return null;

            // Parse into 1-2 short lines
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

                // Skip duplicates
                bool isDup = false;
                foreach (var recent in _recentMessages)
                {
                    if (ComputeSimilarity(line, recent) > 0.7) { isDup = true; break; }
                }
                if (isDup) continue;

                result.Add(line);
                _recentMessages.Add(line);
                if (_recentMessages.Count > 20) _recentMessages.RemoveAt(0);
            }

            if (result.Count == 0) return null;

            MainFile.Logger?.Info($"[ChatEngine] Conv turn ({_characterName}): {result.Count} lines — {string.Join(" | ", result)}");
            return result.ToArray();
        }
        catch (TaskCanceledException)
        {
            MainFile.Logger?.Info("[ChatEngine] Conv request timed out");
            return null;
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Info($"[ChatEngine] Conv error: {ex.Message}");
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
