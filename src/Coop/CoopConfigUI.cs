using System;
using Godot;

namespace TokenSpire2.Coop;

/// <summary>
/// Godot UI for configuring auto-battle and co-op mode.
/// Attached to the scene tree root, renders a small config button
/// in the top-left corner that expands into a config panel.
/// Press T to toggle auto-battle pause/resume.
/// </summary>
public partial class CoopConfigUI : Control
{
    // ── UI nodes ───────────────────────────────────────────────────────
    private Button _toggleButton = null!;
    private Panel _configPanel = null!;
    private CheckButton _autoBattleCheck = null!;
    private CheckButton _coopModeCheck = null!;
    private OptionButton _scopeOption = null!;
    private Label _statusLabel = null!;
    private Label _tKeyHint = null!;
    private bool _panelVisible;

    // ── Constants ───────────────────────────────────────────────────────
    private const float ButtonSize = 32f;
    private const float PanelWidth = 240f;
    private const float PanelHeight = 200f;
    private const float Margin = 8f;

    public override void _Ready()
    {
        Name = "CoopConfigUI";
        MouseFilter = MouseFilterEnum.Ignore;

        BuildUI();
        UpdateUIState();

        // Update every 0.5s to reflect state changes
        var timer = new Godot.Timer();
        timer.WaitTime = 0.5;
        timer.OneShot = false;
        timer.Timeout += UpdateUIState;
        AddChild(timer);
        timer.Start();
    }

    public override void _Input(InputEvent @event)
    {
        // T-key toggles auto-battle pause/resume
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.T)
            {
                bool isPaused = CoopManager.TogglePause();
                if (isPaused)
                    MainFile.Logger?.Info("[Coop] ⏸ Auto-battle PAUSED (T-key)");
                else
                    MainFile.Logger?.Info("[Coop] ▶ Auto-battle RESUMED (T-key)");
                UpdateUIState();
            }
        }
    }

    // ── UI Construction ─────────────────────────────────────────────────

    private void BuildUI()
    {
        // ── Toggle button (top-left corner) ─────────────────────────────
        _toggleButton = new Button();
        _toggleButton.Text = "⚙";
        _toggleButton.Position = new Vector2(Margin, Margin);
        _toggleButton.CustomMinimumSize = new Vector2(ButtonSize, ButtonSize);
        _toggleButton.Flat = true;
        _toggleButton.Pressed += OnToggleButtonPressed;
        _toggleButton.TooltipText = "TokenSpire2 Config (T=暂停/恢复)";
        AddChild(_toggleButton);

        // ── Config panel ────────────────────────────────────────────────
        _configPanel = new Panel();
        _configPanel.Position = new Vector2(Margin, Margin + ButtonSize + 4f);
        _configPanel.CustomMinimumSize = new Vector2(PanelWidth, PanelHeight);
        _configPanel.Visible = false;

        var panelStyle = new StyleBoxFlat();
        panelStyle.BgColor = new Color(0.1f, 0.1f, 0.15f, 0.92f);
        panelStyle.BorderWidthLeft = panelStyle.BorderWidthRight = 1;
        panelStyle.BorderWidthTop = panelStyle.BorderWidthBottom = 1;
        panelStyle.BorderColor = new Color(0.4f, 0.4f, 0.5f, 1f);
        panelStyle.CornerRadiusTopLeft = panelStyle.CornerRadiusTopRight = 6;
        panelStyle.CornerRadiusBottomLeft = panelStyle.CornerRadiusBottomRight = 6;
        _configPanel.AddThemeStyleboxOverride("panel", panelStyle);
        AddChild(_configPanel);

        // Container for panel contents
        var container = new VBoxContainer();
        container.Position = new Vector2(10, 8);
        container.CustomMinimumSize = new Vector2(PanelWidth - 20, 0);
        _configPanel.AddChild(container);

        // Title
        var title = new Label();
        title.Text = "TokenSpire2 配置";
        title.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 1f));
        title.AddThemeFontSizeOverride("font_size", 16);
        container.AddChild(title);
        container.AddChild(CreateSpacer(6));

        // ── Auto-battle checkbox ────────────────────────────────────────
        var autoRow = new HBoxContainer();
        _autoBattleCheck = new CheckButton();
        _autoBattleCheck.ButtonPressed = CoopManager.Config.AutoBattleEnabled;
        _autoBattleCheck.Pressed += () =>
        {
            CoopManager.SetAutoBattleEnabled(_autoBattleCheck.ButtonPressed);
            UpdateUIState();
        };
        autoRow.AddChild(_autoBattleCheck);
        var autoLabel = new Label();
        autoLabel.Text = " 自动战斗";
        autoLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f, 1f));
        autoRow.AddChild(autoLabel);
        container.AddChild(autoRow);
        container.AddChild(CreateSpacer(4));

        // ── Co-op mode checkbox ─────────────────────────────────────────
        var coopRow = new HBoxContainer();
        _coopModeCheck = new CheckButton();
        _coopModeCheck.ButtonPressed = CoopManager.Config.CoopMode;
        _coopModeCheck.Pressed += () =>
        {
            CoopManager.SetCoopMode(_coopModeCheck.ButtonPressed);
            UpdateUIState();
        };
        coopRow.AddChild(_coopModeCheck);
        var coopLabel = new Label();
        coopLabel.Text = " 多人模式";
        coopLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f, 1f));
        coopRow.AddChild(coopLabel);
        container.AddChild(coopRow);
        container.AddChild(CreateSpacer(4));

        // ── Scope dropdown ──────────────────────────────────────────────
        var scopeRow = new HBoxContainer();
        var scopeLabel = new Label();
        scopeLabel.Text = "范围: ";
        scopeLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f, 1f));
        scopeRow.AddChild(scopeLabel);

        _scopeOption = new OptionButton();
        _scopeOption.AddItem("仅战斗");
        _scopeOption.AddItem("全部(战斗+事件+奖励)");
        _scopeOption.Selected = (int)CoopManager.Config.AutoBattleScope;
        _scopeOption.ItemSelected += (idx) =>
        {
            CoopManager.SetAutoBattleScope((AutoBattleScope)idx);
            UpdateUIState();
        };
        scopeRow.AddChild(_scopeOption);
        container.AddChild(scopeRow);
        container.AddChild(CreateSpacer(8));

        // ── Status label ────────────────────────────────────────────────
        _statusLabel = new Label();
        _statusLabel.Text = "状态: 运行中";
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.5f, 1f, 0.5f, 1f));
        _statusLabel.AddThemeFontSizeOverride("font_size", 12);
        container.AddChild(_statusLabel);

        // ── T-key hint ──────────────────────────────────────────────────
        _tKeyHint = new Label();
        _tKeyHint.Text = "按 T 键暂停/恢复自动战斗";
        _tKeyHint.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f, 1f));
        _tKeyHint.AddThemeFontSizeOverride("font_size", 11);
        container.AddChild(_tKeyHint);

        _panelVisible = false;
    }

    private static Control CreateSpacer(float height)
    {
        var spacer = new Control();
        spacer.CustomMinimumSize = new Vector2(0, height);
        return spacer;
    }

    // ── Event handlers ──────────────────────────────────────────────────

    private void OnToggleButtonPressed()
    {
        _panelVisible = !_panelVisible;
        _configPanel.Visible = _panelVisible;
        _toggleButton.Text = _panelVisible ? "✕" : "⚙";
    }

    private void UpdateUIState()
    {
        var config = CoopManager.Config;

        if (config.AutoBattlePaused)
        {
            _statusLabel.Text = "⏸ 已暂停 (按T恢复)";
            _statusLabel.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.3f, 1f));
        }
        else if (!config.AutoBattleEnabled)
        {
            _statusLabel.Text = "⏹ 已关闭";
            _statusLabel.AddThemeColorOverride("font_color", new Color(1f, 0.4f, 0.4f, 1f));
        }
        else
        {
            var scopeText = config.AutoBattleScope == AutoBattleScope.Full ? "全部" : "仅战斗";
            _statusLabel.Text = $"▶ 运行中 ({scopeText})";
            _statusLabel.AddThemeColorOverride("font_color", new Color(0.5f, 1f, 0.5f, 1f));
        }

        _autoBattleCheck.ButtonPressed = config.AutoBattleEnabled;
        _coopModeCheck.ButtonPressed = config.CoopMode;
        _scopeOption.Selected = (int)config.AutoBattleScope;
    }
}
