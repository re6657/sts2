using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TokenSpire2.Launcher;

public partial class MainForm : Form
{
    // ── Paths ────────────────────────────────────────────────────────────
    private static string FindGameRoot()
    {
        string? dir = null;
        try
        {
            dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "SlayTheSpire2.exe"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
        try
        {
            dir = Environment.CurrentDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "SlayTheSpire2.exe"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
        MessageBox.Show("找不到 SlayTheSpire2.exe", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.Exit(1);
        return @".";
    }

    private static readonly string GameDir = FindGameRoot();
    private static readonly string GameExe = Path.Combine(GameDir, "SlayTheSpire2.exe");
    private static readonly string ModDir  = Path.Combine(GameDir, @"mods\TokenSpire2");

    // ── Game character data ──────────────────────────────────────────────
    private static readonly string[] GameChars = { "IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER", "RANDOM" };
    private static readonly string[] GameNames = { "Ironclad 战士", "Silent 猎手", "Defect 机器人", "Regent 君王", "Necrobinder 亡灵", "🎲 随机" };
    private static readonly string[] AllChars  = { "IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER" };
    private static readonly string[] AllNames  = { "Ironclad 战士", "Silent 猎手", "Defect 机器人", "Regent 君王", "Necrobinder 亡灵" };

    // ── AI persona data ──────────────────────────────────────────────────
    private string[] _personaIds   = Array.Empty<string>();
    private string[] _personaNames = Array.Empty<string>();

    // ── Controls ─────────────────────────────────────────────────────────
    private ComboBox      _hostCharCombo   = null!;
    private TextBox       _seedBox         = null!;
    private NumericUpDown _botCountSpinner = null!;
    private CheckBox      _hostAutoCheck   = null!;
    private Label         _windowCountLabel = null!;

    private readonly List<ComboBox> _botGameCharCombos = new();
    private readonly List<Label>    _botGameCharLabels = new();

    private TextBox       _apiKeyBox          = null!;
    private CheckBox      _aiChatEnabledCheck  = null!;

    private readonly List<ComboBox> _botPersonaCombos = new();
    private readonly List<Label>    _botPersonaLabels = new();

    private Button  _launchBtn = null!;
    private TextBox _logBox    = null!;

    // Dynamic panels whose heights change with bot count
    private Panel _botGamePanel = null!;
    private Panel _botPersonaPanel = null!;
    private Panel _aiSettingsPanel = null!;

    // ── Layout constants ─────────────────────────────────────────────────
    private const int FORM_W  = 820;
    private const int FORM_H  = 820;
    private const int MARGIN  = 16;
    private const int ROW_H   = 34;
    private const int GAP     = 10;

    private Color BgDark   = Color.FromArgb(38, 38, 44);
    private Color BgPanel  = Color.FromArgb(50, 50, 58);
    private Color BgInput  = Color.FromArgb(60, 60, 70);
    private Color TextMain = Color.FromArgb(230, 230, 235);
    private Color TextDim  = Color.FromArgb(150, 150, 160);
    private Color Accent   = Color.FromArgb(70, 140, 230);

    public MainForm()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Text = "TokenSpire2 多人对战启动器";
        Size = new Size(FORM_W, FORM_H);
        MinimumSize = new Size(FORM_W, 700);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = BgDark;
        ForeColor = TextMain;
        Font = new Font("Microsoft YaHei UI", 9.5f);

        // ── Main scrollable container ────────────────────────────────────
        var scrollPanel = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(FORM_W - 16, FORM_H - 40),
            AutoScroll = true,
            BackColor = BgDark,
            BorderStyle = BorderStyle.None,
        };
        Controls.Add(scrollPanel);

        int cy = 16; // current Y inside scroll panel
        int pw = scrollPanel.ClientSize.Width - MARGIN * 2; // panel content width

        // ═══════════════════════════════════════════════════════════════════
        // Title
        // ═══════════════════════════════════════════════════════════════════
        var title = new Label
        {
            Text = "TokenSpire2  LAN 多人对战启动器",
            Location = new Point(MARGIN, cy),
            Size = new Size(pw, 36),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold),
            ForeColor = Accent,
        };
        scrollPanel.Controls.Add(title);
        cy += 48;

        // ── Load data BEFORE building any dropdowns ────────────────────
        ScanPersonas();
        LoadApiKeyFromConfig();

        // ═══════════════════════════════════════════════════════════════════
        // Section: 玩家设置
        // ═══════════════════════════════════════════════════════════════════
        var lblPlayers = SectionLabel("🎮  玩家设置");
        lblPlayers.Location = new Point(MARGIN, cy);
        scrollPanel.Controls.Add(lblPlayers);
        cy += 28;

        var playerPanel = new Panel
        {
            Location = new Point(MARGIN, cy),
            Size = new Size(pw, 140),
            BackColor = BgPanel,
            Padding = new Padding(14, 10, 14, 10),
        };
        scrollPanel.Controls.Add(playerPanel);

        var playerTable = new TableLayoutPanel
        {
            Location = new Point(8, 8),
            Size = new Size(playerPanel.Width - 16, playerPanel.Height - 16),
            ColumnCount = 3,
            RowCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        playerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        playerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        playerTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        playerPanel.Controls.Add(playerTable);

        // Row 0: Host character
        playerTable.Controls.Add(RowLabel("Host 角色"), 0, 0);
        _hostCharCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280, BackColor = BgInput, ForeColor = TextMain, FlatStyle = FlatStyle.Flat };
        for (int i = 0; i < AllChars.Length; i++) _hostCharCombo.Items.Add($"{AllChars[i]}  —  {AllNames[i]}");
        _hostCharCombo.SelectedIndex = 0;
        playerTable.Controls.Add(_hostCharCombo, 1, 0);

        // Row 1: Seed
        playerTable.Controls.Add(RowLabel("种子"), 0, 1);
        _seedBox = new TextBox { Width = 200, PlaceholderText = "留空 = 随机", BackColor = BgInput, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle };
        playerTable.Controls.Add(_seedBox, 1, 1);

        // Row 2: Bot count
        playerTable.Controls.Add(RowLabel("Bot 数量"), 0, 2);
        _botCountSpinner = new NumericUpDown { Minimum = 1, Maximum = 3, Value = 2, Width = 60, BackColor = BgInput, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle };
        playerTable.Controls.Add(_botCountSpinner, 1, 2);

        _hostAutoCheck = new CheckBox { Text = "Host 也自动战斗（纯 AI 对战）", FlatStyle = FlatStyle.Flat, AutoSize = true, ForeColor = TextMain };
        playerTable.Controls.Add(_hostAutoCheck, 2, 2);

        // Row 3: Window count
        _windowCountLabel = new Label { ForeColor = TextDim, AutoSize = true, Font = new Font(Font.FontFamily, 8.5f) };
        playerTable.Controls.Add(_windowCountLabel, 1, 3);
        UpdateWindowCountLabel();

        _botCountSpinner.ValueChanged += (_, _) =>
        {
            int n = (int)_botCountSpinner.Value;
            UpdateWindowCountLabel();
            RebuildBotGameCharDropdowns(n);
            RebuildBotPersonaDropdowns(n);
            RepositionAllSections();
        };

        cy += playerPanel.Height + GAP;

        // ═══════════════════════════════════════════════════════════════════
        // Section: Bot 游戏角色
        // ═══════════════════════════════════════════════════════════════════
        var lblBotGame = SectionLabel("⚔️  每个 Bot 的游戏角色");
        lblBotGame.Location = new Point(MARGIN, cy);
        scrollPanel.Controls.Add(lblBotGame);
        cy += 28;

        _botGamePanel = new Panel
        {
            Location = new Point(MARGIN, cy),
            Size = new Size(pw, 120),
            BackColor = BgPanel,
            Padding = new Padding(14, 10, 14, 10),
        };
        scrollPanel.Controls.Add(_botGamePanel);

        RebuildBotGameCharDropdowns((int)_botCountSpinner.Value);
        cy += _botGamePanel.Height + GAP;

        // ═══════════════════════════════════════════════════════════════════
        // Section: AI 对话设置
        // ═══════════════════════════════════════════════════════════════════
        var lblAi = SectionLabel("🤖  AI 对话设置");
        lblAi.Location = new Point(MARGIN, cy);
        scrollPanel.Controls.Add(lblAi);
        cy += 28;

        _aiSettingsPanel = new Panel
        {
            Location = new Point(MARGIN, cy),
            Size = new Size(pw, 220),
            BackColor = BgPanel,
            Padding = new Padding(14, 10, 14, 10),
        };
        scrollPanel.Controls.Add(_aiSettingsPanel);

        // AI fixed section
        var aiTable = new TableLayoutPanel
        {
            Location = new Point(8, 8),
            Size = new Size(pw - 32, 100),
            ColumnCount = 2,
            RowCount = 3,
        };
        aiTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        aiTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _aiSettingsPanel.Controls.Add(aiTable);

        aiTable.Controls.Add(RowLabel("API Key"), 0, 0);
        _apiKeyBox = new TextBox { Width = 420, PlaceholderText = "sk-...  留空则读取 aichat_config.json", BackColor = BgInput, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle };
        aiTable.Controls.Add(_apiKeyBox, 1, 0);

        _aiChatEnabledCheck = new CheckBox { Text = "启用 AI 对话（关闭则显示 喵喵喵）", Checked = true, FlatStyle = FlatStyle.Flat, AutoSize = true, ForeColor = TextMain };
        aiTable.Controls.Add(_aiChatEnabledCheck, 1, 1);

        var manageBtn = new Button
        {
            Text = "📂  管理角色文件",
            Width = 140, Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = BgInput,
            ForeColor = TextMain,
        };
        manageBtn.Click += (_, _) =>
        {
            var d = Path.Combine(ModDir, "characters");
            if (!Directory.Exists(d)) Directory.CreateDirectory(d);
            Process.Start("explorer.exe", d);
        };
        aiTable.Controls.Add(manageBtn, 1, 2);

        // Bot persona sub-section
        var personaLabel = new Label
        {
            Text = "每个 Bot 的 AI 人格:",
            Location = new Point(16, 108),
            AutoSize = true,
            Font = new Font(Font.FontFamily, 9f, FontStyle.Underline),
            ForeColor = TextMain,
        };
        _aiSettingsPanel.Controls.Add(personaLabel);

        _botPersonaPanel = new Panel
        {
            Location = new Point(28, 132),
            Size = new Size(pw - 60, 90),
            BackColor = Color.Transparent,
        };
        _aiSettingsPanel.Controls.Add(_botPersonaPanel);

        RebuildBotPersonaDropdowns((int)_botCountSpinner.Value);

        cy += _aiSettingsPanel.Height + GAP;

        // ═══════════════════════════════════════════════════════════════════
        // Launch button
        // ═══════════════════════════════════════════════════════════════════
        _launchBtn = new Button
        {
            Text = "🚀  启动多人对战",
            Location = new Point(MARGIN, cy),
            Size = new Size(pw, 48),
            Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
            BackColor = Color.FromArgb(0, 140, 230),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        _launchBtn.FlatAppearance.BorderSize = 0;
        _launchBtn.Click += OnLaunchClick;
        scrollPanel.Controls.Add(_launchBtn);
        cy += 58;

        // ═══════════════════════════════════════════════════════════════════
        // Log
        // ═══════════════════════════════════════════════════════════════════
        _logBox = new TextBox
        {
            Location = new Point(MARGIN, cy),
            Size = new Size(pw, 200),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(28, 28, 32),
            ForeColor = Color.FromArgb(130, 210, 130),
            Font = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.FixedSingle,
        };
        scrollPanel.Controls.Add(_logBox);
        cy += 210;

    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private Label SectionLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
        ForeColor = Accent,
    };

    private Label RowLabel(string text) => new()
    {
        Text = text,
        TextAlign = ContentAlignment.MiddleRight,
        AutoSize = true,
        ForeColor = TextMain,
        Padding = new Padding(0, 6, 8, 0),
    };

    private void UpdateWindowCountLabel()
    {
        int n = (int)_botCountSpinner.Value;
        _windowCountLabel.Text = $"共启动 {n + 1} 个窗口  —  1 Host + {n} Bot{(n > 1 ? "s" : "")}";
    }

    // ── Reposition sections after bot count change ────────────────────────

    private void RepositionAllSections()
    {
        // Find the scroll panel and reposition sections below bot count
        // The bot game panel and AI panel heights change, so we need to recalculate
        var scrollPanel = Controls[0] as Panel;
        if (scrollPanel == null) return;

        // Recalculate all Y positions
        int cy = 16 + 48; // title
        int pw = scrollPanel.ClientSize.Width - MARGIN * 2;

        // Player settings panel
        cy += 28; // section label
        var playerPanel = scrollPanel.Controls.OfType<Panel>().FirstOrDefault(p => p.BackColor == BgPanel && p.Top < 300);
        if (playerPanel != null) cy += playerPanel.Height + GAP;

        // Bot game section
        cy += 28; // section label
        _botGamePanel.Location = new Point(MARGIN, cy);
        cy += _botGamePanel.Height + GAP;

        // AI settings section
        cy += 28; // section label
        _aiSettingsPanel.Location = new Point(MARGIN, cy);
        int aiH = 108 + 24 + _botPersonaPanel.Height + 20; // table + persona label + persona panel
        _aiSettingsPanel.Height = aiH;
        _botPersonaPanel.Location = new Point(28, 132);
        cy += _aiSettingsPanel.Height + GAP;

        // Launch button
        _launchBtn.Location = new Point(MARGIN, cy);
        cy += 58;

        // Log
        _logBox.Location = new Point(MARGIN, cy);
    }

    // ── Bot game character dropdowns ─────────────────────────────────────

    private void RebuildBotGameCharDropdowns(int botCount)
    {
        _botGamePanel.Controls.Clear();
        foreach (var c in _botGameCharCombos) c.Dispose();
        foreach (var l in _botGameCharLabels) l.Dispose();
        _botGameCharCombos.Clear();
        _botGameCharLabels.Clear();

        var table = new TableLayoutPanel
        {
            Location = new Point(8, 8),
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        for (int i = 0; i < botCount; i++)
        {
            var lbl = new Label
            {
                Text = $"Bot {i + 1}:",
                TextAlign = ContentAlignment.MiddleRight,
                AutoSize = true,
                ForeColor = TextMain,
                Padding = new Padding(0, 5, 8, 0),
            };
            table.Controls.Add(lbl, 0, i);
            _botGameCharLabels.Add(lbl);

            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 350,
                BackColor = BgInput,
                ForeColor = TextMain,
                FlatStyle = FlatStyle.Flat,
            };
            for (int j = 0; j < GameChars.Length; j++)
                combo.Items.Add($"{GameChars[j]}  —  {GameNames[j]}");
            combo.SelectedIndex = Math.Min(i, GameChars.Length - 1);
            table.Controls.Add(combo, 1, i);
            _botGameCharCombos.Add(combo);
        }

        _botGamePanel.Controls.Add(table);
        _botGamePanel.Height = table.Height + 16;
    }

    private string GetBotGameCharacter(int idx)
    {
        if (idx >= 0 && idx < _botGameCharCombos.Count)
        {
            int sel = _botGameCharCombos[idx].SelectedIndex;
            if (sel >= 0 && sel < GameChars.Length) return GameChars[sel];
        }
        return "RANDOM";
    }

    // ── AI persona dropdowns ─────────────────────────────────────────────

    private void ScanPersonas()
    {
        try
        {
            var d = Path.Combine(ModDir, "characters");
            if (!Directory.Exists(d))
            {
                Directory.CreateDirectory(d);
                _personaIds   = new[] { "delilah", "seele", "elysia" };
                _personaNames = new[] { "德丽莎·月下初拥", "希儿·Vollerei", "爱莉希雅" };
                return;
            }
            var ids = new List<string>();
            var names = new List<string>();
            foreach (var f in Directory.GetFiles(d, "*.md"))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                if (string.IsNullOrEmpty(id) || id.Equals("TEMPLATE", StringComparison.OrdinalIgnoreCase)) continue;
                string display = id;
                try
                {
                    var first = File.ReadLines(f).FirstOrDefault()?.Trim() ?? "";
                    if (first.StartsWith("# ")) first = first[2..];
                    int slash = first.IndexOf('/');
                    if (slash >= 0) first = first[(slash + 1)..];
                    int dash = first.IndexOf("——");
                    if (dash < 0) dash = first.IndexOf('—');
                    if (dash >= 0) first = first[..dash];
                    int paren = first.IndexOf('（');
                    if (paren >= 0) first = first[..paren];
                    first = first.Trim();
                    if (first.Length > 0 && first.Length < 30) display = first;
                }
                catch { }
                ids.Add(id);
                names.Add(display);
            }
            if (ids.Count == 0)
            {
                ids.AddRange(new[] { "delilah", "seele", "elysia" });
                names.AddRange(new[] { "德丽莎·月下初拥", "希儿·Vollerei", "爱莉希雅" });
            }
            _personaIds   = ids.ToArray();
            _personaNames = names.ToArray();
        }
        catch
        {
            _personaIds   = new[] { "delilah", "seele", "elysia" };
            _personaNames = new[] { "德丽莎·月下初拥", "希儿·Vollerei", "爱莉希雅" };
        }
    }

    private void RebuildBotPersonaDropdowns(int botCount)
    {
        _botPersonaPanel.Controls.Clear();
        foreach (var c in _botPersonaCombos) c.Dispose();
        foreach (var l in _botPersonaLabels) l.Dispose();
        _botPersonaCombos.Clear();
        _botPersonaLabels.Clear();

        var table = new TableLayoutPanel
        {
            Location = new Point(0, 0),
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        for (int i = 0; i < botCount; i++)
        {
            var lbl = new Label
            {
                Text = $"Bot {i + 1}:",
                TextAlign = ContentAlignment.MiddleRight,
                AutoSize = true,
                ForeColor = TextMain,
                Padding = new Padding(0, 5, 8, 0),
            };
            table.Controls.Add(lbl, 0, i);
            _botPersonaLabels.Add(lbl);

            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 350,
                BackColor = BgInput,
                ForeColor = TextMain,
                FlatStyle = FlatStyle.Flat,
            };
            for (int j = 0; j < _personaIds.Length; j++)
                combo.Items.Add($"{_personaIds[j]}  —  {_personaNames[j]}");
            combo.SelectedIndex = Math.Min(i, _personaIds.Length - 1);
            table.Controls.Add(combo, 1, i);
            _botPersonaCombos.Add(combo);
        }

        _botPersonaPanel.Controls.Add(table);
        _botPersonaPanel.Height = table.Height;

        // Resize AI panel
        if (_aiSettingsPanel != null)
            _aiSettingsPanel.Height = 108 + 28 + _botPersonaPanel.Height + 16;
    }

    private string GetBotPersona(int idx)
    {
        if (idx >= 0 && idx < _botPersonaCombos.Count)
        {
            int sel = _botPersonaCombos[idx].SelectedIndex;
            if (sel >= 0 && sel < _personaIds.Length) return _personaIds[sel];
        }
        return "delilah";
    }

    // ── API key ──────────────────────────────────────────────────────────

    private void LoadApiKeyFromConfig()
    {
        try
        {
            var p = Path.Combine(ModDir, "aichat_config.json");
            if (File.Exists(p))
            {
                var json = File.ReadAllText(p);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("ApiKey", out var k))
                {
                    var v = k.GetString();
                    if (!string.IsNullOrEmpty(v)) _apiKeyBox.Text = v;
                }
            }
        }
        catch { }
    }

    private void SaveApiKeyToConfig(string key)
    {
        try
        {
            var p = Path.Combine(ModDir, "aichat_config.json");
            var dict = new Dictionary<string, object>();
            if (File.Exists(p))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        dict[prop.Name] = prop.Value.GetString()!;
                    else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                        dict[prop.Name] = prop.Value.GetDouble();
                    else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.True
                          || prop.Value.ValueKind == System.Text.Json.JsonValueKind.False)
                        dict[prop.Name] = prop.Value.GetBoolean();
                }
            }
            dict["ApiKey"] = key;
            File.WriteAllText(p, System.Text.Json.JsonSerializer.Serialize(dict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ── Log ──────────────────────────────────────────────────────────────

    private void Log(string msg)
    {
        if (_logBox.InvokeRequired) { _logBox.Invoke(() => Log(msg)); return; }
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
    }

    // ── Launch ───────────────────────────────────────────────────────────

    private async void OnLaunchClick(object? sender, EventArgs e)
    {
        _launchBtn.Enabled = false;
        _launchBtn.Text = "⏳  正在启动...";
        _logBox.Clear();

        try
        {
            string hostChar = AllChars[_hostCharCombo.SelectedIndex];
            string seed     = _seedBox.Text.Trim();
            int    botCount = (int)_botCountSpinner.Value;
            bool   hostAuto = _hostAutoCheck.Checked;
            bool   aiOn     = _aiChatEnabledCheck.Checked;

            Log("══════════════════════════════════════");
            Log($"Host 角色  : {hostChar}");
            Log($"种子       : {(string.IsNullOrEmpty(seed) ? "随机" : seed)}");
            Log($"Bot 数量   : {botCount}");
            Log($"Host 自动  : {(hostAuto ? "是" : "否")}");
            Log($"AI 对话    : {(aiOn ? "启用" : "关闭")}");
            for (int i = 0; i < botCount; i++)
            {
                string gc = GetBotGameCharacter(i);
                string ai = GetBotPersona(i);
                Log($"  Bot {i + 1} : 游戏角色={gc}   AI人格={ai}");
            }
            Log("══════════════════════════════════════");

            if (!string.IsNullOrWhiteSpace(_apiKeyBox.Text))
                SaveApiKeyToConfig(_apiKeyBox.Text.Trim());

            if (!File.Exists(GameExe))
            {
                Log($"❌ 找不到游戏: {GameExe}");
                return;
            }

            Log("清理旧配置...");
            CleanupOldFiles(botCount);

            string seedJson = string.IsNullOrEmpty(seed) ? "null" : $"\"{seed}\"";
            string hostSig  = "config_read_host.signal";
            string hostPath = Path.Combine(GameDir, "token_spire_host.json");
            string hostCfg  = $$"""{"Seed":{{seedJson}},"Character":"{{hostChar}}","MultiplayerMode":true,"IsMultiplayerHost":true,"SteamPersonaName":"Player","AutoBattleEnabled":{{hostAuto.ToString().ToLower()}},"SignalFile":"{{hostSig}}"}""";
            File.WriteAllText(hostPath, hostCfg);
            Log($"✓ Host 配置已写入");

            var bots = new List<(string path, string sig, string name)>();
            for (int i = 1; i <= botCount; i++)
            {
                string sig  = $"config_read_bot{i}.signal";
                string path = Path.Combine(GameDir, $"token_spire_bot{i}.json");
                string name = $"Bot{i}";
                string gc   = GetBotGameCharacter(i - 1);
                string ai   = GetBotPersona(i - 1);
                string cfg  = $$"""{"Seed":{{seedJson}},"Character":"{{gc}}","MultiplayerMode":true,"IsMultiplayerHost":false,"SteamPersonaName":"{{name}}","AutoBattleEnabled":true,"SignalFile":"{{sig}}","AiChatEnabled":{{aiOn.ToString().ToLower()}},"AiChatCharacter":"{{ai}}"}""";
                File.WriteAllText(path, cfg);
                bots.Add((path, sig, name));
                Log($"✓ {name} 配置: 游戏={gc}  AI={ai}");
            }

            Log("");
            Log("正在启动所有窗口...");

            Process.Start(new ProcessStartInfo
            {
                FileName = GameExe, Arguments = $"--fastmp host_standard --config \"{hostPath}\"",
                WorkingDirectory = GameDir, UseShellExecute = false,
            });
            Log("  Host 窗口已启动");

            foreach (var b in bots)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = GameExe, Arguments = $"--fastmp join --config \"{b.path}\"",
                    WorkingDirectory = GameDir, UseShellExecute = false,
                });
                Log($"  {b.name} 窗口已启动");
            }

            Log("");
            Log("══════════════════════════════════════");
            Log($"✅ 全部 {1 + botCount} 个窗口已启动");
            Log($"  Host    : 127.0.0.1:33771 (ENet)");
            for (int i = 0; i < bots.Count; i++)
                Log($"  {bots[i].name} : 自动加入 + 自动战斗");
            Log("══════════════════════════════════════");

            MessageBox.Show(
                $"✅ 启动完成！\n\nHost + {botCount} Bot  共 {1 + botCount} 个窗口\n\nHost: 手动操作\nBot:  全程自动战斗 + AI 对话",
                "TokenSpire2", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log($"❌ 错误: {ex.Message}");
            MessageBox.Show($"启动失败:\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _launchBtn.Enabled = true;
            _launchBtn.Text = "🚀  启动多人对战";
        }
    }

    private static void CleanupOldFiles(int maxBots)
    {
        try
        {
            File.Delete(Path.Combine(ModDir, "config_read.signal"));
            File.Delete(Path.Combine(ModDir, "config_read_host.signal"));
            File.Delete(Path.Combine(ModDir, "batch_config.json"));
            File.Delete(Path.Combine(GameDir, "token_spire_host.json"));
            for (int i = 1; i <= maxBots; i++)
            {
                File.Delete(Path.Combine(ModDir, $"config_read_bot{i}.signal"));
                File.Delete(Path.Combine(GameDir, $"token_spire_bot{i}.json"));
            }
        }
        catch { }
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
