using System.Diagnostics;
using System.Text.Json;
using TokenSpire2.Diagnostics;

namespace TokenSpire2.Launcher;

public sealed class MainForm : Form
{
    private static readonly Color Canvas = Color.FromArgb(18, 20, 24);
    private static readonly Color Sidebar = Color.FromArgb(24, 27, 32);
    private static readonly Color Surface = Color.FromArgb(31, 35, 41);
    private static readonly Color Input = Color.FromArgb(40, 45, 53);
    private static readonly Color Accent = Color.FromArgb(224, 157, 65);
    private static readonly Color TextMain = Color.FromArgb(238, 239, 242);
    private static readonly Color TextMuted = Color.FromArgb(154, 160, 170);

    private static readonly string[] GameChars = { "IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER" };
    private static readonly string[] GameNames = { "Ironclad 战士", "Silent 猎手", "Defect 机器人", "Regent 君王", "Necrobinder 亡灵" };
    private static readonly string[] PersonaIds = { "delilah", "seele", "elysia" };
    private static readonly string[] PersonaNames = { "德丽莎·月下初拥", "希儿·Vollerei", "爱莉希雅" };

    private static readonly string GameDir = FindGameRoot();
    private static readonly string GameExe = Path.Combine(GameDir, "SlayTheSpire2.exe");
    private static readonly string ModDir = Path.Combine(GameDir, "mods", "TokenSpire2");
    private static readonly string DiagnosticDir = Path.Combine(ModDir, "logs");

    private readonly Dictionary<string, Panel> _pages = new();
    private readonly List<Button> _navButtons = new();
    private readonly List<ComboBox> _botCharacters = new();
    private readonly List<ComboBox> _botPersonas = new();
    private readonly DiagnosticLogReader _diagnosticReader = new();

    private ComboBox _hostCharacter = null!;
    private NumericUpDown _botCount = null!;
    private TextBox _seed = null!;
    private CheckBox _chatEnabled = null!;
    private TextBox _apiKey = null!;
    private FlowLayoutPanel _botCards = null!;
    private TextBox _activityLog = null!;
    private Button _launch = null!;
    private ComboBox _roleFilter = null!;
    private ComboBox _eventFilter = null!;
    private ListView _diagnosticList = null!;
    private TextBox _diagnosticDetail = null!;
    private System.Windows.Forms.Timer _diagnosticTimer = null!;

    public MainForm()
    {
        Text = "TokenSpire2 Control Room";
        Size = new Size(1180, 760);
        MinimumSize = new Size(980, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Canvas;
        ForeColor = TextMain;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        BuildShell();
        LoadApiKey();
        RebuildBotCards();
        ShowPage("大厅");
    }

    private void BuildShell()
    {
        var nav = new Panel { Dock = DockStyle.Left, Width = 190, BackColor = Sidebar, Padding = new Padding(14, 22, 14, 14) };
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Canvas, Padding = new Padding(24) };
        Controls.Add(content);
        Controls.Add(nav);

        var brand = new Label { Text = "TOKENSPIRE 2\nCONTROL ROOM", AutoSize = false, Height = 70, Dock = DockStyle.Top, ForeColor = Accent, Font = new Font(Font.FontFamily, 13, FontStyle.Bold) };
        nav.Controls.Add(brand);

        foreach (var name in new[] { "大厅", "Bot", "对话", "诊断", "设置" }.Reverse())
        {
            var button = new Button
            {
                Text = name, Dock = DockStyle.Top, Height = 48, FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0),
                BackColor = Sidebar, ForeColor = TextMuted, Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => ShowPage(name);
            nav.Controls.Add(button);
            _navButtons.Add(button);
        }

        foreach (var name in new[] { "大厅", "Bot", "对话", "诊断", "设置" })
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Canvas, AutoScroll = true, Visible = false };
            content.Controls.Add(page);
            _pages[name] = page;
        }

        BuildLobby(_pages["大厅"]);
        BuildBots(_pages["Bot"]);
        BuildChat(_pages["对话"]);
        BuildDiagnostics(_pages["诊断"]);
        BuildSettings(_pages["设置"]);
    }

    private void BuildLobby(Panel page)
    {
        AddHeader(page, "多人大厅", "Host 永久保持全手动；自动策略仅运行在 Bot 客户端。", 0);
        var card = Card(0, 88, 900, 210);
        page.Controls.Add(card);

        _hostCharacter = Combo(GameNames, 0, 24, 52, 260);
        _seed = TextInput("留空则随机", 310, 52, 260);
        _botCount = new NumericUpDown { Location = new Point(596, 52), Size = new Size(110, 34), Minimum = 1, Maximum = 3, Value = 2, BackColor = Input, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle };
        _botCount.ValueChanged += (_, _) => RebuildBotCards();
        card.Controls.AddRange(new Control[] {
            Caption("Host 角色", 24, 24), _hostCharacter, Caption("游戏种子", 310, 24), _seed,
            Caption("Bot 数量", 596, 24), _botCount,
            InfoBadge("HOST · 全手动", 24, 112, Color.FromArgb(55, 88, 67)),
            InfoBadge("全局卡牌选择器：禁用", 182, 112, Color.FromArgb(67, 57, 43))
        });

        _launch = new Button { Text = "启动多人对战", Location = new Point(0, 322), Size = new Size(240, 48), BackColor = Accent, ForeColor = Color.FromArgb(24, 24, 24), FlatStyle = FlatStyle.Flat, Font = new Font(Font, FontStyle.Bold) };
        _launch.FlatAppearance.BorderSize = 0;
        _launch.Click += OnLaunchClick;
        page.Controls.Add(_launch);

        _activityLog = new TextBox { Location = new Point(0, 390), Size = new Size(900, 240), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Surface, ForeColor = TextMuted, BorderStyle = BorderStyle.None, Font = new Font("Cascadia Mono", 9) };
        page.Controls.Add(_activityLog);
    }

    private void BuildBots(Panel page)
    {
        AddHeader(page, "Bot 编队", "为每个客户端分配游戏角色与对话人格。", 0);
        _botCards = new FlowLayoutPanel { Location = new Point(0, 88), Size = new Size(930, 560), AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Canvas };
        page.Controls.Add(_botCards);
    }

    private void BuildChat(Panel page)
    {
        AddHeader(page, "对话", "对话采用后台预生成与匀速投递，避免集中爆发和长时间空窗。", 0);
        var card = Card(0, 88, 900, 240);
        page.Controls.Add(card);
        _chatEnabled = new CheckBox { Text = "启用 Bot 人格对话", Location = new Point(24, 28), AutoSize = true, Checked = true, ForeColor = TextMain };
        _apiKey = TextInput("DeepSeek API Key", 24, 92, 600, true);
        card.Controls.AddRange(new Control[] { _chatEnabled, Caption("API Key", 24, 68), _apiKey });
        card.Controls.Add(new Label { Text = "人格由 characters/*.md 驱动；游戏内容只是聊天背景，人物关系与陪伴感优先。", Location = new Point(24, 150), AutoSize = true, ForeColor = TextMuted });
    }

    private void BuildDiagnostics(Panel page)
    {
        AddHeader(page, "诊断中心", "Host/Bot 分流日志；自动聚焦跳过回合、不同步、卡死与 Host 自动化拦截。", 0);
        _roleFilter = Combo(new[] { "全部", "host", "bot1", "bot2", "bot3" }, 0, 0, 88, 160);
        _eventFilter = Combo(new[] { "全部", DiagnosticEventTypes.TurnSkippedWithPlayableCards, DiagnosticEventTypes.TurnReadinessWait, DiagnosticEventTypes.StateDivergence, DiagnosticEventTypes.ActionQueueStalled, DiagnosticEventTypes.OverlayStuck, DiagnosticEventTypes.HostAutomationBlocked }, 0, 180, 88, 320);
        var refresh = new Button { Text = "刷新", Location = new Point(520, 88), Size = new Size(92, 34), BackColor = Accent, ForeColor = Color.Black, FlatStyle = FlatStyle.Flat };
        refresh.FlatAppearance.BorderSize = 0;
        refresh.Click += (_, _) => RefreshDiagnostics();
        page.Controls.AddRange(new Control[] { _roleFilter, _eventFilter, refresh });

        _diagnosticList = new ListView { Location = new Point(0, 140), Size = new Size(900, 260), View = View.Details, FullRowSelect = true, BackColor = Surface, ForeColor = TextMain, BorderStyle = BorderStyle.None };
        _diagnosticList.Columns.Add("时间", 140); _diagnosticList.Columns.Add("实例", 80); _diagnosticList.Columns.Add("事件", 260); _diagnosticList.Columns.Add("回合", 60); _diagnosticList.Columns.Add("摘要", 330);
        _diagnosticList.SelectedIndexChanged += (_, _) => ShowDiagnosticDetail();
        _diagnosticDetail = new TextBox { Location = new Point(0, 420), Size = new Size(900, 210), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, BackColor = Color.FromArgb(14, 16, 19), ForeColor = TextMuted, BorderStyle = BorderStyle.None, Font = new Font("Cascadia Mono", 9) };
        page.Controls.AddRange(new Control[] { _diagnosticList, _diagnosticDetail });
        _diagnosticTimer = new System.Windows.Forms.Timer { Interval = 3000, Enabled = true };
        _diagnosticTimer.Tick += (_, _) => { if (_pages["诊断"].Visible) RefreshDiagnostics(); };
    }

    private void BuildSettings(Panel page)
    {
        AddHeader(page, "设置", "运行目录与安全策略。", 0);
        var card = Card(0, 88, 900, 180);
        card.Controls.AddRange(new Control[] {
            Caption("游戏目录", 24, 24), new Label { Text = GameDir, Location = new Point(24, 52), AutoSize = true, ForeColor = TextMuted },
            Caption("Host 策略", 24, 96), new Label { Text = "固定全手动（不可在启动器中启用自动控制）", Location = new Point(24, 124), AutoSize = true, ForeColor = Accent }
        });
        page.Controls.Add(card);
    }

    private void RebuildBotCards()
    {
        if (_botCards == null) return;
        _botCards.Controls.Clear(); _botCharacters.Clear(); _botPersonas.Clear();
        for (var i = 0; i < (int)_botCount.Value; i++)
        {
            var card = Card(0, 0, 870, 128);
            card.Margin = new Padding(0, 0, 0, 14);
            card.Controls.Add(new Label { Text = $"BOT {i + 1}", Location = new Point(20, 18), AutoSize = true, ForeColor = Accent, Font = new Font(Font, FontStyle.Bold) });
            var game = Combo(GameNames, i % GameNames.Length, 150, 46, 260);
            var persona = Combo(PersonaNames, i % PersonaNames.Length, 450, 46, 280);
            card.Controls.AddRange(new Control[] { Caption("游戏角色", 150, 18), game, Caption("对话人格", 450, 18), persona });
            _botCharacters.Add(game); _botPersonas.Add(persona); _botCards.Controls.Add(card);
        }
    }

    private void ShowPage(string name)
    {
        foreach (var page in _pages) page.Value.Visible = page.Key == name;
        foreach (var button in _navButtons)
        {
            var selected = button.Text == name;
            button.BackColor = selected ? Surface : Sidebar;
            button.ForeColor = selected ? Accent : TextMuted;
        }
        if (name == "诊断") RefreshDiagnostics();
    }

    private void RefreshDiagnostics()
    {
        var events = _diagnosticReader.Read(DiagnosticDir, _roleFilter.Text, _eventFilter.Text);
        _diagnosticList.BeginUpdate(); _diagnosticList.Items.Clear();
        foreach (var evt in events)
        {
            var item = new ListViewItem(evt.TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"));
            item.SubItems.Add(evt.InstanceRole); item.SubItems.Add(evt.EventType); item.SubItems.Add(evt.TurnNumber.ToString()); item.SubItems.Add(evt.Message); item.Tag = evt;
            if (evt.EventType is DiagnosticEventTypes.TurnSkippedWithPlayableCards or DiagnosticEventTypes.StateDivergence) item.ForeColor = Color.FromArgb(255, 105, 105);
            else if (evt.Severity == "warning") item.ForeColor = Accent;
            _diagnosticList.Items.Add(item);
        }
        _diagnosticList.EndUpdate();
    }

    private void ShowDiagnosticDetail()
    {
        if (_diagnosticList.SelectedItems.Count == 0 || _diagnosticList.SelectedItems[0].Tag is not DiagnosticEvent evt) return;
        _diagnosticDetail.Text = JsonSerializer.Serialize(evt, new JsonSerializerOptions { WriteIndented = true });
    }

    private void OnLaunchClick(object? sender, EventArgs e)
    {
        _launch.Enabled = false; _activityLog.Clear();
        try
        {
            var botCount = (int)_botCount.Value;
            var sessionId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
            SaveApiKey(); CleanupOldFiles(3);
            var seedJson = string.IsNullOrWhiteSpace(_seed.Text) ? null : _seed.Text.Trim();
            var hostPath = WriteConfig("host", true, GameChars[_hostCharacter.SelectedIndex], "Player", seedJson, false, false, "", sessionId);
            var bots = new List<string>();
            for (var i = 0; i < botCount; i++)
                bots.Add(WriteConfig($"bot{i + 1}", false, GameChars[_botCharacters[i].SelectedIndex], $"Bot{i + 1}", seedJson, true, _chatEnabled.Checked, PersonaIds[_botPersonas[i].SelectedIndex], sessionId));

            Log($"会话 {sessionId}：Host 全手动，{botCount} 个 Bot 自动运行");
            StartGame(hostPath, "--fastmp host_standard");
            foreach (var path in bots) StartGame(path, "--fastmp join");
            Log($"已启动 {botCount + 1} 个游戏窗口。诊断日志将按 host/bot 分流。");
        }
        catch (Exception ex) { Log("启动失败：" + ex); MessageBox.Show(ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { _launch.Enabled = true; }
    }

    private string WriteConfig(string role, bool host, string character, string persona, string? seed, bool auto, bool chat, string chatCharacter, string sessionId)
    {
        var path = Path.Combine(GameDir, $"token_spire_{role}.json");
        var cfg = new { Seed = seed, Character = character, MultiplayerMode = true, IsMultiplayerHost = host, SteamPersonaName = persona, AutoBattleEnabled = auto, AiChatEnabled = chat, AiChatCharacter = chatCharacter, InstanceRole = role, SessionId = sessionId, SignalFile = $"config_read_{role}.signal" };
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static void StartGame(string config, string mode) => Process.Start(new ProcessStartInfo { FileName = GameExe, Arguments = $"{mode} --config \"{config}\"", WorkingDirectory = GameDir, UseShellExecute = false });
    private void Log(string text) => _activityLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n");

    private void LoadApiKey()
    {
        try { var path = Path.Combine(ModDir, "aichat_config.json"); if (File.Exists(path) && JsonDocument.Parse(File.ReadAllText(path)).RootElement.TryGetProperty("ApiKey", out var key)) _apiKey.Text = key.GetString() ?? ""; } catch { }
    }
    private void SaveApiKey()
    {
        if (string.IsNullOrWhiteSpace(_apiKey.Text)) return;
        var path = Path.Combine(ModDir, "aichat_config.json");
        Dictionary<string, object?> cfg = new();
        try { if (File.Exists(path)) cfg = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(path)) ?? cfg; } catch { }
        cfg["ApiKey"] = _apiKey.Text.Trim(); File.WriteAllText(path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CleanupOldFiles(int maxBots)
    {
        foreach (var role in new[] { "host", "bot1", "bot2", "bot3" })
        {
            try { File.Delete(Path.Combine(ModDir, $"config_read_{role}.signal")); } catch { }
            try { File.Delete(Path.Combine(GameDir, $"token_spire_{role}.json")); } catch { }
        }
    }

    private static string FindGameRoot()
    {
        var dir = Path.GetDirectoryName(typeof(MainForm).Assembly.Location);
        for (var i = 0; i < 12 && dir != null; i++, dir = Path.GetDirectoryName(dir)) if (File.Exists(Path.Combine(dir, "SlayTheSpire2.exe"))) return dir;
        dir = Environment.CurrentDirectory;
        for (var i = 0; i < 12 && dir != null; i++, dir = Path.GetDirectoryName(dir)) if (File.Exists(Path.Combine(dir, "SlayTheSpire2.exe"))) return dir;
        return Environment.CurrentDirectory;
    }

    private static Panel Card(int x, int y, int w, int h) => new() { Location = new Point(x, y), Size = new Size(w, h), BackColor = Surface, Padding = new Padding(16) };
    private static Label Caption(string text, int x, int y) => new() { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = TextMuted };
    private static Label InfoBadge(string text, int x, int y, Color color) => new() { Text = text, Location = new Point(x, y), AutoSize = true, BackColor = color, ForeColor = TextMain, Padding = new Padding(10, 6, 10, 6) };
    private static TextBox TextInput(string placeholder, int x, int y, int w, bool password = false) => new() { PlaceholderText = placeholder, Location = new Point(x, y), Size = new Size(w, 34), BackColor = Input, ForeColor = TextMain, BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = password };
    private static ComboBox Combo(IEnumerable<string> items, int selected, int x, int y, int w)
    {
        var c = new ComboBox { Location = new Point(x, y), Size = new Size(w, 34), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Input, ForeColor = TextMain, FlatStyle = FlatStyle.Flat };
        c.Items.AddRange(items.Cast<object>().ToArray()); c.SelectedIndex = Math.Clamp(selected, 0, c.Items.Count - 1); return c;
    }
    private static void AddHeader(Control page, string title, string subtitle, int y)
    {
        page.Controls.Add(new Label { Text = title, Location = new Point(0, y), AutoSize = true, Font = new Font("Microsoft YaHei UI", 22, FontStyle.Bold), ForeColor = TextMain });
        page.Controls.Add(new Label { Text = subtitle, Location = new Point(2, y + 48), AutoSize = true, ForeColor = TextMuted });
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
