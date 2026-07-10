// ═══════════════════════════════════════════════════════════════
// PATCH ARCHITECTURE DOCUMENTATION
// ═══════════════════════════════════════════════════════════════
//
// Patch installation happens in TWO layers:
//
// LAYER 1: [ModuleInitializer] in AutoSlayPatch.cs
//   - TokenSpire2ModuleInit runs during assembly load (BEFORE ModInitializer)
//   - Calls LocalCoopPatchInstaller.Install() with Harmony("localcoop.transport-broker")
//   - Installs 25+ broker patch types from src/Coop/Patches/
//   - THIS IS THE ACTIVE PATCH SYSTEM — all broker/LAN patches live here
//
// LAYER 2: ModInitializer in MainFile.cs
//   - Calls new Harmony("TokenSpire2").PatchAll() as fallback
//   - Catches any [HarmonyPatch] classes not registered by Layer 1
//   - Currently catches AttachAutoSlayNodePatch (NGame._Ready hook)
//
// PLANNED MIGRATION (not yet executed):
//   The patch files in src/Coop/Patches/ and src/Coop/Runtime/ will
//   eventually be consolidated into src/Patches/ and src/Broker/.
//   This file documents what each consolidated patch class will contain.
//
// Migration source for each consolidation target:
//   UnlockAllPatch          ← AutoSlayNode.UnlockAll() (runtime, not Harmony)
//   SteamCrashPatch         ← SteamCrashSuppressionPatch.cs
//   StartupBypassPatches    ← BrokerHostStartupBypassPatches.cs + BrokerClientStartupBypassPatches.cs
//   LobbyServicePatch       ← BrokerLobbyServiceSubstitutionPatch.cs
//   JoinFlowPatch           ← BrokerClientJoinFlowPatch.cs
//   LobbyTransitionPatch    ← BrokerForceLobbyTransitionPatch.cs + BrokerBeginRunPatch.cs
//   RunIdentityPatch        ← RunIdentityLaunchPatch.cs + 9 RunIdentity*.cs files
//   ControllerPatches       ← ControllerInputOwnershipPatches.cs + SteamControllerInputSelectionPatches.cs
//   LocalMultiControlPatches ← RunIdentityDualRoleAdventureGuardPatch.cs + 5 other guard patches
// ═══════════════════════════════════════════════════════════════

namespace TokenSpire2.Patches;

/// <summary>
/// This file documents the planned patch consolidation.
/// No actual Harmony attributes are present — all broker patches
/// are currently installed by LocalCoopPatchInstaller.Install()
/// called from AutoSlayPatch.TokenSpire2ModuleInit [ModuleInitializer].
///
/// Once the migration is executed, each class below will contain
/// the real [HarmonyPatch] annotations and patch methods.
/// </summary>
public static class PatchMigrationNotes
{
    public const string BrokersHarmonyId = "localcoop.transport-broker";
    public const string ModHarmonyId = "TokenSpire2";

    // See file header for migration source notes.
}
