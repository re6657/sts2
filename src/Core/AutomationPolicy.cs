namespace TokenSpire2.Core;

/// <summary>
/// Every automatic interaction must pass through this policy. In multiplayer,
/// the host is always human-controlled regardless of toggle state.
/// </summary>
public enum AutomationAction
{
    RegisterCardSelector,
    Combat,
    CardGrid,
    Map,
    Event,
    Shop,
    Rest,
    Rewards,
    LlmSelection,
    TimeoutFallback,
}

public sealed class AutomationPolicy
{
    private readonly bool _multiplayer;
    private readonly bool _isHost;
    private readonly bool _autoNavigate;
    private readonly bool _autoBattle;
    private readonly bool _autoEvent;

    public AutomationPolicy(
        bool multiplayer,
        bool isHost,
        bool autoNavigate,
        bool autoBattle,
        bool autoEvent)
    {
        _multiplayer = multiplayer;
        _isHost = isHost;
        _autoNavigate = autoNavigate;
        _autoBattle = autoBattle;
        _autoEvent = autoEvent;
    }

    public bool IsFullyManualHost => _multiplayer && _isHost;

    public bool Allows(AutomationAction action)
    {
        if (IsFullyManualHost)
            return false;

        return action switch
        {
            AutomationAction.Combat => _autoBattle,
            AutomationAction.Map or AutomationAction.Shop or AutomationAction.Rest => _autoNavigate,
            AutomationAction.CardGrid or AutomationAction.Event or AutomationAction.Rewards => _autoEvent,
            // ICardSelector is process-global. During multiplayer replay it is
            // consulted for every player, not only the local bot, so installing
            // it on a client makes that client preselect the human host's cards
            // and immediately diverge from the host state.
            AutomationAction.RegisterCardSelector => !_multiplayer && (_autoBattle || _autoEvent),
            AutomationAction.LlmSelection or AutomationAction.TimeoutFallback => _autoEvent,
            _ => false,
        };
    }
}
