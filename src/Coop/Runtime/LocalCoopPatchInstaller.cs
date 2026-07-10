using System.Linq;
using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Patches;

namespace LocalCoop.Mod.Runtime;

public static class LocalCoopPatchInstaller
{
    /// <summary>
    /// Patches that intercept Steam/ENet networking.
    /// MUST be skipped when SteamFix64 is active — the native DLL proxy
    /// handles networking transparently, and these Harmony patches would conflict.
    /// </summary>
    public static readonly Type[] BrokerNetworkPatchTypes =
    [
        typeof(BrokerClientJoinFlowPatch),
        // Broker Virtual Friend Steam patches: inject "人机一号" into the Steam
        // friend list at the API level. The game's ShowFriends creates a real
        // NJoinFriendButton. Player clicks it → normal JoinGameAsync flow →
        // BrokerClientJoinFlowPatch intercepts JoinFlow.Begin → broker handshake.
        // UNBROKEN CHAIN: JoinResult returns to JoinGameAsync → scene transition.
        // (Replaces the architecturally-broken BrokerJoinFriendScreenPatch.)
        typeof(BrokerVFPatch_GetFriendCount),
        typeof(BrokerVFPatch_GetFriendByIndex),
        typeof(BrokerVFPatch_GetFriendPersonaName),
        typeof(BrokerVFPatch_GetFriendGamePlayed),
        typeof(BrokerHostSteamStartupBypassPatch),
        typeof(BrokerHostENetStartupBypassPatch),
        // Old client bypass patches (BrokerClientSteamStartupBypassPatch,
        // BrokerClientENetStartupBypassPatch, BrokerClientConnectBypassPatch)
        // targeted NetClientGameService methods that don't exist in the current
        // STS2 version. Replaced with patches that target the actual P2P
        // connection initializers:
        typeof(BrokerClientSteamConnectBypassPatch),
        typeof(BrokerClientENetConnectBypassPatch),
        typeof(BrokerForceLobbyTransitionPatch),
        typeof(BrokerLobbyServiceSubstitutionPatch),
        typeof(BrokerBeginRunPatch)
    ];

    /// <summary>
    /// Patches for dual-role UI/input isolation and Steam crash suppression.
    /// These are ALWAYS needed when running two instances (broker OR SteamFix64).
    /// They handle input ownership, UI alignment, reward isolation, etc.
    /// </summary>
    public static readonly Type[] DualRolePatchTypes =
    [
        typeof(SteamCrashSuppressionPatch),
        typeof(SteamControllerInputSelectionPatches),
        typeof(ControllerInputOwnershipPatches),
        typeof(RunIdentityLaunchPatch),
        typeof(RunIdentityDualRoleAdventureVoidGuardPatch),
        typeof(RunIdentityDualRoleAdventureBoolGuardPatch),
        typeof(RunIdentityLocalUiAlignmentPatch),
        typeof(RunIdentityRewardAlignmentPatch),
        typeof(RunIdentityPotionAnimationGuardPatch),
        typeof(RunIdentityRelicInventoryVisualGuardPatch),
        typeof(RunIdentityRemoteEventUiGuardPatch),
        typeof(RunIdentityLocalActionGuardPatch),
        typeof(RunIdentityRemoteMutationGuardPatch),
        typeof(RunIdentityRemoteMutationTaskGuardPatch)
    ];

    /// <summary>Combined list — used in broker mode (all patches).</summary>
    private static readonly Type[] DefaultPatchTypes =
        BrokerNetworkPatchTypes.Concat(DualRolePatchTypes).ToArray();

    private static readonly Type[] RunIdentityDiagnosticsPatchTypes =
    [
        typeof(RunIdentityLifecycleDiagnosticsPatches),
        typeof(OneOffSynchronizerDiagnosticsPatches),
        typeof(OneOffSynchronizerLocalPlayerDiagnosticsPatch),
        typeof(CardSelectCmdDiagnosticsPatches),
        typeof(CardSelectCmdShouldSelectLocalDiagnosticsPatch),
        typeof(LocalContextIsMeDiagnosticsPatch),
        typeof(PlayerChoiceSynchronizerDiagnosticsPatches),
        typeof(RewardDiagnosticsPatches),
        typeof(RewardStateDiagnosticsPatches),
        typeof(PotionProcurementDiagnosticsPatches),
        typeof(RunIdentityLocalUiDiagnosticsPatches),
        typeof(PeerInputOwnershipDiagnosticsPatches)
    ];

    public static IReadOnlyList<Type> DefaultPatchTypesForTesting => DefaultPatchTypes;

    public static IReadOnlyList<Type> RunIdentityDiagnosticsPatchTypesForTesting => RunIdentityDiagnosticsPatchTypes;

    public static IReadOnlyList<Type> PatchTypesFor(bool includeRunIdentityDiagnostics)
    {
        return includeRunIdentityDiagnostics
            ? DefaultPatchTypes.Concat(RunIdentityDiagnosticsPatchTypes).ToArray()
            : DefaultPatchTypes;
    }

    public static LocalCoopPatchInstallResult Install(
        Assembly assembly,
        Action<string> log,
        Action<Type>? installPatchType = null)
    {
        return Install(assembly, DefaultPatchTypes, log, installPatchType);
    }

    /// <summary>
    /// Install ONLY dual-role patches (no broker networking patches).
    /// Used when SteamFix64 handles networking at the DLL level.
    /// </summary>
    public static LocalCoopPatchInstallResult InstallDualRoleOnly(
        Assembly assembly,
        Action<string> log)
    {
        return Install(assembly, DualRolePatchTypes, log, installPatchType: null);
    }

    public static LocalCoopPatchInstallResult Install(
        Assembly assembly,
        IReadOnlyList<Type> patchTypes,
        Action<string> log,
        Action<Type>? installPatchType = null)
    {
        Harmony? harmony = null;
        var installer = installPatchType ?? (patchType =>
        {
            harmony ??= new Harmony("localcoop.transport-broker");
            InstallPatchType(patchType, harmony);
        });
        var failures = new List<string>();

        foreach (var patchType in patchTypes)
        {
            try
            {
                installer(patchType);
                log($"Harmony patch installed: {patchType.FullName ?? patchType.Name}.");
            }
            catch (Exception exception)
            {
                var message = $"Harmony patch failed: {patchType.FullName ?? patchType.Name}: {exception.GetType().Name}: {exception.Message}";
                failures.Add(message);
                log(message);
            }
        }

        return new LocalCoopPatchInstallResult(failures.Count == 0, failures);
    }

    private static void InstallPatchType(Type patchType, Harmony harmony)
    {
        harmony.CreateClassProcessor(patchType).Patch();
    }
}

public sealed record LocalCoopPatchInstallResult(bool Success, IReadOnlyList<string> Failures);
