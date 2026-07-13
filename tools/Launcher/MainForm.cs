using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace TokenSpire2.Launcher;

/// <summary>
/// TokenSpire2 GUI Launcher — 无需命令行的傻瓜式多人启动器
///
/// 用法：
///   1. 双击 TokenSpire2Launcher.exe
///   2. 选择角色、种子（可选）、Bot 数量
///   3. 点击「启动」
///   4. 自动生成配置文件、顺序启动窗口、等待就绪信号
///
/// 等价于手动执行：
///   .\launch_lan.ps1 -Character IRONCLAD -BotCount 2
/// </summary>
public partial class MainForm : Form
{
    // ── Paths ────────────────────────────────────────────────────────────
    // Compute paths relative to the launcher executable.
    // Launcher is at: mods/TokenSpire2/tools/Launcher/bin/<config>/<tfm>/
    // Walk up until we find "Slay the Spire 2" root.
    private static string FindGameRoot()
    {
        string? dir = null;
        try
        {
            // Try 1: walk up from assembly location
            dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "SlayTheSpire2.exe")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { /* fall through */ }

        try
        {
            // Try 2: walk up from current directory
            dir = Environment.CurrentDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "SlayTheSpire2.exe")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { /* fall through */ }

        // Try 3: walk up from AppContext.BaseDirectory
        try
        {
            dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "SlayTheSpire2.exe")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { /* fall through */ }

        // Last resort
        string msg = "Cannot find SlayTheSpire2.exe. Please place the launcher in the mod's tools/Launcher folder.";
        try { MessageBox.Show(msg, "TokenSpire2 Launcher Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        catch { System.Console.Error.WriteLine(msg); }
        Environment.Exit(1);
        return @"."; // unreachable
    }
    private static readonly string GameDir = FindGameRoot();
    private static readonly string GameExe = Path.Combine(GameDir, "SlayTheSpire2.exe");
    private static readonly string ModDir = Path.Combine(GameDir, @"mods\TokenSpire2");

    // ── Controls ─────────────────────────────────────────────────────────
    private ComboBox _charCombo = null!;
    private TextBox _seedBox = null!;
    private NumericUpDown _botCountSpinner = null!;
    private Button _launchBtn = null!;
    private TextBox _logBox = null!;
    private CheckBox _autoBattleHostCheck = null!;

    // ── AI Chat controls ──────────────────────────────────────────────
    private TextBox _apiKeyBox = null!;
    private CheckBox _aiChatEnabledCheck = null!;
    private readonly List<ComboBox> _botCharCombos = new();
    private readonly List<Label> _botCharLabels = new();
    private string[] _availableCharacters = Array.Empty<string>();
    private string[] _availableCharDisplayNames = Array.Empty<string>();

    // ── Bot game character dropdowns ───────────────────────────────────
    private readonly List<ComboBox> _botGameCharCombos = new();
    private readonly List<Label> _botGameCharLabels = new();
    private static readonly string[] GameCharacters = { "IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER", "RANDOM" };
    private static readonly string[] GameCharNames = { "Ironclad 战士", "Silent 刺客", "Defect 机器人", "Regent 君王", "Necrobinder 亡灵", "随机" };

    // ── Character list ───────────────────────────────────────────────────
    private static readonly string[] Characters = { "IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER" };
    private static readonly string[] CharNames = { "Ironclad 战士", "Silent 刺客", "Defect 机器人", "Regent 君王", "Necrobinder 亡灵" };

    public MainForm()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Text = "TokenSpire2 多人启动器";
        Size = new System.Drawing.Size(520, 800);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        int y = 15;

        // ── Title ────────────────────────────────────────────────────────
        var title = new Label
        {
            Text = "TokenSpire2 LAN 多人对战启动器",
            Font = new System.Drawing.Font("Microsoft YaHei", 14, System.Drawing.FontStyle.Bold),
            Location = new System.Drawing.Point(15, y),
            Size = new System.Drawing.Size(480, 30),
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
        };
        Controls.Add(title);
        y += 40;

        // ── Character ────────────────────────────────────────────────────
        var charLabel = new Label
        {
            Text = "角色选择:",
            Location = new System.Drawing.Point(15, y),
            Size = new System.Drawing.Size(80, 25),
        };
        Controls.Add(charLabel);

        _charCombo = new ComboBox
        {
            Location = new System.Drawing.Point(100, y),
            Size = new System.Drawing.Size(200, 25),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        for (int i = 0; i < Characters.Length; i++)
            _charCombo.Items.Add($"{Characters[i]} — {CharNames[i]}");
        _charCombo.SelectedIndex = 0;
        Controls.Add(_charCombo);
        y += 35;

        // ── Seed ─────────────────────────────────────────────────────────
        var seedLabel = new Label
        {
            Text = "种子 (可选):",
            Location = new System.Drawing.Point(15, y),
            Size = new System.Drawing.Size(80, 25),
        };
        Controls.Add(seedLabel);

        _seedBox = new TextBox
        {
            Location = new System.Drawing.Point(100, y),
            Size = new System.Drawing.Size(200, 25),
            PlaceholderText = "留空=随机种子",
        };
        Controls.Add(_seedBox);
        y += 35;

        // ── Bot Count ────────────────────────────────────────────────────
        var botLabel = new Label
        {
            Text = "Bot 数量:",
            Location = new System.Drawing.Point(15, y),
            Size = new System.Drawing.Size(80, 25),
        };
        Controls.Add(botLabel);

        _botCountSpinner = new NumericUpDown
        {
            Location = new System.Drawing.Point(100, y),
            Size = new System.Drawing.Size(80, 25),
            Minimum = 1,
            Maximum = 3,
            Value = 2,
        };
        Controls.Add(_botCountSpinner);
        y += 35;

        // ── Auto-battle host ─────────────────────────────────────────────
        _autoBattleHostCheck = new CheckBox
        {
            Text = "Host 也开启自动战斗（纯 AI 对战）",
            Location = new System.Drawing.Point(100, y),
            Size = new System.Drawing.Size(300, 25),
            Checked = false,
        };
        Controls.Add(_autoBattleHostCheck);
        y += 35;

        // ── Total windows info ───────────────────────────────────────────
        var infoLabel = new Label
        {
            Text = "将启动窗口: 1 (Host) + Bot = 共 N 窗口",
            Location = new System.Drawing.Point(100, y),
            Size = new System.Drawing.Size(380, 25),
            ForeColor = System.Drawing.Color.Gray,
        };
        Controls.Add(infoLabel);

        _botCountSpinner.ValueChanged += (_, _) =>
        {
            int bots = (int)_botCountSpinner.Value;
            infoLabel.Text = $"将启动窗口: 1 (Host) + {bots} Bot = 共 {bots + 1} 窗口";
            UpdateBotGameCharacterDropdowns(bots);
            UpdateBotCharacterDropdowns(bots);
        };
        y += 35;

        // ── Bot game character section ──────────────────────────────────
        var botGameCharHeader = new Label
        {
            Text = "🎮 Bot 游戏角色:",
            Font = new System.Drawing.Font("Microsoft YaHei", 10, System.Drawing.FontStyle.Bold),
            Location = new System.Drawing.Point(15, y),
            Size = new System.Drawing.Size(480, 25),
        };
        Controls.Add(botGameCharHeader);
        y += 30;

        // Reserve space for bot game character dropdowns (1-3 rows)
        int _botGameCharY = y;
        y += 90; // 3 rows × 30px

        // ── AI Chat section ────────────────────────────────────────────
        var aiSectionLabel = new Label
        {
            Text = "🤖 AI 对话设置",
            Font = new System.Drawing.Font("Microsoft YaHei", 10, System.Drawing.FontStyle.Bold),
            Location = new System.Drawing.Point(15, y),
            Size = new System.Drawing.Size(480, 25),
        };
        Controls.Add(aiSectionLabel);
        y += 30;

        // API Key
        var apiKeyLabel = new Label
        {
            Text = "API Key:",
            Location = new System.Drawing.Point(15, y),
            Size = new System.Drawing.Size(80, 25),
        };
        Controls.Add(apiKeyLabel);

        _apiKeyBox = new TextBox
        {
            Location = new System.Drawing.Point(100, y),
            Size = new System.Drawing.Size(300, 25),
            PlaceholderText = "sk-... (留空使用 aichat_config.json 中的 Key)",
            PasswordChar = '*',
        };
        Controls.Add(_apiKeyBox);
        y += 35;

        // Enable AI Chat checkbox
        _aiChatEnabledCheck = new CheckBox
        {
            Text = "启用 AI 对话（关闭则使用 喵喵喵）",
            Location = new System.Drawing.Point(100, y),
            Size = new System.Drawing.Size(300, 25),
            Checked = true,
        };
        Controls.Add(_aiChatEnabledCheck);
        y += 35;

        // Bot character label header
        var botCharHeaderLabel = new Label
        {
            Text = "Bot 角色分配:",
            Location = new System.Drawing.Point(15, y),
            Size = new System.Drawing.Size(100, 25),
        };
        Controls.Add(botCharHeaderLabel);
        y += 30;

        // Bot character dropdowns (dynamic, created in UpdateBotCharacterDropdowns)
        // Placeholder labels + combos will be added below
        var botCharPanelY = y;

        // "Manage characters" button
        y += 100; // reserve space for 3 dropdown rows
        var manageBtn = new Button
        {
            Text = "📂 管理角色文件",
            Location = new System.Drawing.Point(100, y),
            Size = new System.Drawing.Size(150, 30),
            Font = new System.Drawing.Font("Microsoft YaHei", 9),
        };
        manageBtn.Click += (_, _) =>
        {
            var charsDir = Path.Combine(ModDir, "characters");
            if (!Directory.Exists(charsDir))
                Directory.CreateDirectory(charsDir);
            System.Diagnostics.Process.Start("explorer.exe", charsDir);
        };
        Controls.Add(manageBtn);
        y += 40;

        // ── Launch button ────────────────────────────────────────────────
        _launchBtn = new Button
        {
            Text = "🚀 启动多人对战",
            Location = new System.Drawing.Point(100, y),
            Size = new System.Drawing.Size(300, 40),
            Font = new System.Drawing.Font("Microsoft YaHei", 11, System.Drawing.FontStyle.Bold),
            BackColor = System.Drawing.Color.FromArgb(0, 120, 212),
            ForeColor = System.Drawing.Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        _launchBtn.Click += OnLaunchClick;
        Controls.Add(_launchBtn);
        y += 50;

        // ── Log ──────────────────────────────────────────────────────────
        var logLabel = new Label
        {
            Text = "运行日志:",
            Location = new System.Drawing.Point(15, y),
            Size = new System.Drawing.Size(80, 25),
        };
        Controls.Add(logLabel);
        y += 25;

        _logBox = new TextBox
        {
            Location = new System.Drawing.Point(15, y),
            Size = new System.Drawing.Size(480, 170),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = System.Drawing.Color.FromArgb(30, 30, 30),
            ForeColor = System.Drawing.Color.LightGreen,
            Font = new System.Drawing.Font("Consolas", 9),
        };
        Controls.Add(_logBox);

        // Initial info update
        infoLabel.Text = $"将启动窗口: 1 (Host) + {_botCountSpinner.Value} Bot = 共 {_botCountSpinner.Value + 1} 窗口";

        // Scan available characters and load API key
        ScanAvailableCharacters();
        LoadApiKeyFromConfig();
        UpdateBotGameCharacterDropdowns((int)_botCountSpinner.Value);
        UpdateBotCharacterDropdowns((int)_botCountSpinner.Value);
    }

    /// <summary>
    /// Scan characters/ directory for .md files (excluding TEMPLATE.md).
    /// Populates _availableCharacters and _availableCharDisplayNames.
    /// </summary>
    private void ScanAvailableCharacters()
    {
        try
        {
            var charsDir = Path.Combine(ModDir, "characters");
            if (!Directory.Exists(charsDir))
            {
                Directory.CreateDirectory(charsDir);
                _availableCharacters = new[] { "delilah", "seele", "elysia" };
                _availableCharDisplayNames = new[] { "德丽莎·月下初拥", "希儿·Vollerei", "爱莉希雅" };
                return;
            }

            var files = Directory.GetFiles(charsDir, "*.md");
            var ids = new List<string>();
            var names = new List<string>();

            foreach (var file in files)
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(id) || id.Equals("TEMPLATE", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Try to extract display name from first line
                var displayName = id;
                try
                {
                    var firstLine = File.ReadLines(file).FirstOrDefault()?.Trim() ?? "";
                    if (firstLine.StartsWith("# "))
                        firstLine = firstLine[2..].Trim();

                    var slashIdx = firstLine.IndexOf('/');
                    if (slashIdx >= 0)
                        firstLine = firstLine[(slashIdx + 1)..].Trim();

                    var dashIdx = firstLine.IndexOf("——");
                    if (dashIdx < 0) dashIdx = firstLine.IndexOf("—");
                    if (dashIdx >= 0)
                        firstLine = firstLine[..dashIdx].Trim();

                    var parenIdx = firstLine.IndexOf('（');
                    if (parenIdx >= 0)
                        firstLine = firstLine[..parenIdx].Trim();

                    if (firstLine.Length > 0 && firstLine.Length < 30)
                        displayName = firstLine;
                }
                catch { }

                ids.Add(id);
                names.Add(displayName);
            }

            if (ids.Count == 0)
            {
                ids.AddRange(new[] { "delilah", "seele", "elysia" });
                names.AddRange(new[] { "德丽莎·月下初拥", "希儿·Vollerei", "爱莉希雅" });
            }

            _availableCharacters = ids.ToArray();
            _availableCharDisplayNames = names.ToArray();
        }
        catch
        {
            _availableCharacters = new[] { "delilah", "seele", "elysia" };
            _availableCharDisplayNames = new[] { "德丽莎·月下初拥", "希儿·Vollerei", "爱莉希雅" };
        }
    }

    /// <summary>
    /// Pre-load API key from aichat_config.json so users don't need to re-enter it.
    /// </summary>
    private void LoadApiKeyFromConfig()
    {
        try
        {
            var configPath = Path.Combine(ModDir, "aichat_config.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("ApiKey", out var keyProp))
                {
                    var key = keyProp.GetString();
                    if (!string.IsNullOrEmpty(key))
                        _apiKeyBox.Text = key;
                }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Create/update bot game character dropdowns based on bot count.
    /// </summary>
    private void UpdateBotGameCharacterDropdowns(int botCount)
    {
        foreach (var combo in _botGameCharCombos)
            Controls.Remove(combo);
        foreach (var label in _botGameCharLabels)
            Controls.Remove(label);
        _botGameCharCombos.Clear();
        _botGameCharLabels.Clear();

        int startY = 365; // below "Bot 游戏角色:" header

        for (int i = 0; i < botCount; i++)
        {
            var label = new Label
            {
                Text = $"  Bot {i + 1}:",
                Location = new System.Drawing.Point(30, startY + i * 30),
                Size = new System.Drawing.Size(60, 25),
            };
            Controls.Add(label);
            _botGameCharLabels.Add(label);

            var combo = new ComboBox
            {
                Location = new System.Drawing.Point(95, startY + i * 30),
                Size = new System.Drawing.Size(200, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            for (int j = 0; j < GameCharacters.Length; j++)
                combo.Items.Add($"{GameCharacters[j]} — {GameCharNames[j]}");
            // Default: first bot = different character each, or RANDOM
            combo.SelectedIndex = Math.Min(i, GameCharacters.Length - 1);
            Controls.Add(combo);
            _botGameCharCombos.Add(combo);
        }
    }

    private string GetSelectedBotGameCharacter(int botIndex)
    {
        if (botIndex >= 0 && botIndex < _botGameCharCombos.Count)
        {
            int selIdx = _botGameCharCombos[botIndex].SelectedIndex;
            if (selIdx >= 0 && selIdx < GameCharacters.Length)
                return GameCharacters[selIdx];
        }
        return "RANDOM"; // fallback
    }

    /// <summary>
    /// Create/update bot AI character (persona) dropdowns based on bot count.
    /// </summary>
    private void UpdateBotCharacterDropdowns(int botCount)
    {
        // Remove old dropdowns
        foreach (var combo in _botCharCombos)
            Controls.Remove(combo);
        foreach (var label in _botCharLabels)
            Controls.Remove(label);
        _botCharCombos.Clear();
        _botCharLabels.Clear();

        // Find the Y position (after the botCharHeaderLabel)
        int startY = 310; // fixed position

        for (int i = 0; i < botCount; i++)
        {
            var label = new Label
            {
                Text = $"  Bot {i + 1} 角色:",
                Location = new System.Drawing.Point(30, startY + i * 30),
                Size = new System.Drawing.Size(80, 25),
            };
            Controls.Add(label);
            _botCharLabels.Add(label);

            var combo = new ComboBox
            {
                Location = new System.Drawing.Point(115, startY + i * 30),
                Size = new System.Drawing.Size(200, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            for (int j = 0; j < _availableCharacters.Length; j++)
            {
                combo.Items.Add($"{_availableCharacters[j]} — {_availableCharDisplayNames[j]}");
            }
            // Default: cycle through available characters
            combo.SelectedIndex = Math.Min(i, _availableCharacters.Length - 1);
            Controls.Add(combo);
            _botCharCombos.Add(combo);
        }
    }

    private string GetSelectedBotCharacter(int botIndex)
    {
        if (botIndex >= 0 && botIndex < _botCharCombos.Count)
        {
            int selIdx = _botCharCombos[botIndex].SelectedIndex;
            if (selIdx >= 0 && selIdx < _availableCharacters.Length)
                return _availableCharacters[selIdx];
        }
        return "delilah"; // fallback
    }

    private void Log(string msg)
    {
        if (_logBox.InvokeRequired)
        {
            _logBox.Invoke(() => Log(msg));
            return;
        }
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        _logBox.AppendText($"[{timestamp}] {msg}\r\n");
    }

    private async void OnLaunchClick(object? sender, EventArgs e)
    {
        _launchBtn.Enabled = false;
        _launchBtn.Text = "正在启动...";
        _logBox.Clear();

        try
        {
            string character = Characters[_charCombo.SelectedIndex];
            string seed = _seedBox.Text.Trim();
            int botCount = (int)_botCountSpinner.Value;
            bool hostAutoBattle = _autoBattleHostCheck.Checked;
            bool aiChatEnabled = _aiChatEnabledCheck.Checked;

            Log($"==========================================");
            Log($"角色: {character}");
            Log($"种子: {(string.IsNullOrEmpty(seed) ? "随机" : seed)}");
            Log($"Bot 数量: {botCount}");
            Log($"Host 自动战斗: {(hostAutoBattle ? "是" : "否")}");
            Log($"AI 对话: {(aiChatEnabled ? "启用" : "关闭")}");
            for (int i = 0; i < botCount; i++)
            {
                string gameChar = GetSelectedBotGameCharacter(i);
                string aiPersona = GetSelectedBotCharacter(i);
                Log($"  Bot {i + 1}: 游戏={gameChar}  AI角色={aiPersona}");
            }
            Log($"==========================================");

            // Save API key to aichat_config.json if user entered one
            if (!string.IsNullOrWhiteSpace(_apiKeyBox.Text))
            {
                SaveApiKey(_apiKeyBox.Text.Trim());
            }

            // Verify paths
            if (!File.Exists(GameExe))
            {
                Log($"❌ 错误: 找不到游戏 {GameExe}");
                MessageBox.Show($"找不到游戏执行文件:\n{GameExe}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Clean up old signals and configs
            Log("清理旧文件...");
            CleanupOldFiles(botCount);

            // Write config files
            string seedJson = string.IsNullOrEmpty(seed) ? "null" : $"\"{seed}\"";
            string hostSignal = "config_read_host.signal";
            string hostConfigPath = Path.Combine(GameDir, "token_spire_host.json");
            string hostConfig = $$"""
                {"Seed":{{seedJson}},"Character":"{{character}}","MultiplayerMode":true,"IsMultiplayerHost":true,"SteamPersonaName":"Player","AutoBattleEnabled":{{hostAutoBattle.ToString().ToLower()}},"SignalFile":"{{hostSignal}}"}
                """;
            File.WriteAllText(hostConfigPath, hostConfig);
            Log($"✓ Host 配置: {hostConfigPath}");

            var botConfigs = new List<(string path, string signal, string name)>();
            for (int i = 1; i <= botCount; i++)
            {
                string botSignal = $"config_read_bot{i}.signal";
                string botPath = Path.Combine(GameDir, $"token_spire_bot{i}.json");
                string botName = $"Bot{i}";
                string botGameCharacter = GetSelectedBotGameCharacter(i - 1);
                string botAiPersona = GetSelectedBotCharacter(i - 1);
                string botConfig = $$"""
                    {"Seed":{{seedJson}},"Character":"{{botGameCharacter}}","MultiplayerMode":true,"IsMultiplayerHost":false,"SteamPersonaName":"{{botName}}","AutoBattleEnabled":true,"SignalFile":"{{botSignal}}","AiChatEnabled":{{aiChatEnabled.ToString().ToLower()}},"AiChatCharacter":"{{botAiPersona}}"}
                    """;
                File.WriteAllText(botPath, botConfig);
                botConfigs.Add((botPath, botSignal, botName));
                Log($"✓ {botName} 配置: {botPath} (游戏: {botGameCharacter}, AI: {botAiPersona})");
            }

            // ── Launch ALL windows at once (parallel) ──────────────────
            // Bot has 8s join delay built in — host ENet server will be up by then.
            Log("");
            Log("--- 同时启动所有窗口 ---");

            string hostArgs = $"--fastmp host_standard --config \"{hostConfigPath}\"";
            Log($"Host: {hostArgs}");
            Process.Start(new ProcessStartInfo
            {
                FileName = GameExe,
                Arguments = hostArgs,
                WorkingDirectory = GameDir,
                UseShellExecute = false,
            });

            foreach (var bot in botConfigs)
            {
                string botArgs = $"--fastmp join --config \"{bot.path}\"";
                Log($"{bot.name}: {botArgs}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = GameExe,
                    Arguments = botArgs,
                    WorkingDirectory = GameDir,
                    UseShellExecute = false,
                });
            }

            Log("");
            Log("==========================================");
            Log($"✅ 已启动 {1 + botCount} 个窗口！");
            Log($"  窗口 1 (Host): ENet server 127.0.0.1:33771");
            for (int i = 0; i < botConfigs.Count; i++)
            {
                Log($"  窗口 {i + 2} ({botConfigs[i].name}): 自动加入 + 自动战斗");
            }
            Log("==========================================");

            MessageBox.Show(
                $"启动完成！\n\n" +
                $"Host + {botCount} Bot 共 {1 + botCount} 个窗口同步启动\n\n" +
                $"Host: 手动操作 / Bot: 全程自动",
                "启动完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log($"❌ 错误: {ex.Message}");
            MessageBox.Show($"启动失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _launchBtn.Enabled = true;
            _launchBtn.Text = "🚀 启动多人对战";
        }
    }

    private static void CleanupOldFiles(int maxBots)
    {
        try
        {
            // Remove host files
            File.Delete(Path.Combine(ModDir, "config_read.signal"));
            File.Delete(Path.Combine(ModDir, "config_read_host.signal"));
            File.Delete(Path.Combine(ModDir, "batch_config.json"));
            File.Delete(Path.Combine(GameDir, "token_spire_host.json"));

            // Remove bot files
            for (int i = 1; i <= maxBots; i++)
            {
                File.Delete(Path.Combine(ModDir, $"config_read_bot{i}.signal"));
                File.Delete(Path.Combine(GameDir, $"token_spire_bot{i}.json"));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Cleanup error: {ex.Message}");
        }
    }

    private static async Task<bool> WaitForSignalAsync(string signalPath, int timeoutSeconds)
    {
        int waited = 0;
        while (!File.Exists(signalPath) && waited < timeoutSeconds)
        {
            await Task.Delay(3000);
            waited += 3;
        }
        return File.Exists(signalPath);
    }

    /// <summary>
    /// Save API key to aichat_config.json, preserving other settings.
    /// </summary>
    private static void SaveApiKey(string apiKey)
    {
        try
        {
            var configPath = Path.Combine(ModDir, "aichat_config.json");
            // Read existing config or create default
            string json;
            if (File.Exists(configPath))
            {
                json = File.ReadAllText(configPath);
            }
            else
            {
                json = "{}";
            }

            var doc = System.Text.Json.JsonDocument.Parse(json);
            var obj = new Dictionary<string, object>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("ApiKey"))
                    obj["ApiKey"] = apiKey;
                else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    obj[prop.Name] = prop.Value.GetString()!;
                else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                    obj[prop.Name] = prop.Value.GetDouble();
                else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.True)
                    obj[prop.Name] = true;
                else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.False)
                    obj[prop.Name] = false;
            }
            // Ensure ApiKey is set
            obj["ApiKey"] = apiKey;

            var newJson = System.Text.Json.JsonSerializer.Serialize(obj,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, newJson);
        }
        catch { /* best effort */ }
    }
}

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
