using System;
using System.Diagnostics;
using System.IO;
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
        Size = new System.Drawing.Size(520, 520);
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
        };
        y += 35;

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

            Log($"==========================================");
            Log($"角色: {character}");
            Log($"种子: {(string.IsNullOrEmpty(seed) ? "随机" : seed)}");
            Log($"Bot 数量: {botCount}");
            Log($"Host 自动战斗: {(hostAutoBattle ? "是" : "否")}");
            Log($"==========================================");

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
                string botConfig = $$"""
                    {"Seed":{{seedJson}},"Character":"{{character}}","MultiplayerMode":true,"IsMultiplayerHost":false,"SteamPersonaName":"{{botName}}","AutoBattleEnabled":true,"SignalFile":"{{botSignal}}"}
                    """;
                File.WriteAllText(botPath, botConfig);
                botConfigs.Add((botPath, botSignal, botName));
                Log($"✓ {botName} 配置: {botPath}");
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
