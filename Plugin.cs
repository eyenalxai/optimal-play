using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BlackJacket.OptimalPlay
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class OptimalPlayPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.blackjacket.mods.optimalplay";
        public const string PluginName = "Black Jacket - Optimal Play";
        public const string PluginVersion = "1.1.0";

        internal static ManualLogSource Log;
        internal static Settings Cfg;

        private void Awake()
        {
            Log = Logger;
            Cfg = new Settings(Config);

            try
            {
                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll(typeof(PlayerInputPatches));
                PlayerInputPatches.Patched = true;
                Log.LogInfo("Player turn hooks installed.");
            }
            catch (Exception e)
            {
                Log.LogWarning($"Player turn hooks failed ({e.Message}); using fallback detection.");
            }

            var go = new GameObject("OptimalPlay");
            DontDestroyOnLoad(go);
            go.AddComponent<AutoPilot>();

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }
    }

    /// <summary>
    /// Tracks whether the game is currently waiting for player input in the draw phase.
    /// EnableAreasToTakeCardsFrom is called at the start of the player's turn (and when a
    /// card is returned / sleeved), DisableAreasToTakeCardsFrom on pickup and at turn end.
    /// </summary>
    [HarmonyPatch]
    internal static class PlayerInputPatches
    {
        public static bool Patched;

        [HarmonyPatch(typeof(GameController), "EnableAreasToTakeCardsFrom")]
        [HarmonyPostfix]
        private static void EnablePostfix()
        {
            AutoPilot.PlayerInputActive = true;
        }

        [HarmonyPatch(typeof(GameController), "DisableAreasToTakeCardsFrom")]
        [HarmonyPostfix]
        private static void DisablePostfix()
        {
            AutoPilot.PlayerInputActive = false;
        }
    }

    internal sealed class Settings
    {
        public readonly ConfigEntry<bool> Enabled;
        public readonly ConfigEntry<bool> AutoPlay;
        public readonly ConfigEntry<float> ActionDelay;
        public readonly ConfigEntry<bool> AutoActivateOptionalEffects;
        public readonly ConfigEntry<bool> AutoSelectInsight;
        public readonly ConfigEntry<bool> AutoSelectDemand;
        public readonly ConfigEntry<bool> AutoSelectShuffle;
        public readonly ConfigEntry<bool> LogDecisions;
        public readonly ConfigEntry<bool> ShowStatus;
        public readonly ConfigEntry<KeyboardShortcut> ToggleKey;
        public readonly ConfigEntry<int> SearchNodeBudget;
        public readonly ConfigEntry<int> SearchTimeMs;

        public Settings(ConfigFile file)
        {
            Enabled = file.Bind("General", "Enabled", true,
                "Enable the mod.");

            AutoPlay = file.Bind("General", "AutoPlay", true,
                "Play the draw phase automatically. The toggle key switches this at runtime.");

            ActionDelay = file.Bind("General", "ActionDelay", 0.5f,
                "Seconds to wait between automatic actions, so animations and card effects can finish.");

            AutoActivateOptionalEffects = file.Bind("General", "AutoActivateOptionalEffects", true,
                "When a played card asks whether to activate its optional effect, choose Activate. If false, choose Skip.");

            AutoSelectInsight = file.Bind("Automation", "AutoSelectInsight", true,
                "Automatically reorder insight windows to the arrangement with the best solver outcome.");

            AutoSelectDemand = file.Bind("Automation", "AutoSelectDemand", true,
                "Automatically take the best card in demand windows, or skip when nothing helps.");

            AutoSelectShuffle = file.Bind("Automation", "AutoSelectShuffle", true,
                "Automatically choose which deck to shuffle when a shuffle effect asks.");

            LogDecisions = file.Bind("General", "LogDecisions", true,
                "Log every decision and its evaluation to the BepInEx console/log.");

            ShowStatus = file.Bind("Display", "ShowStatus", true,
                "Show a small status line with the current decision.");

            ToggleKey = file.Bind("General", "ToggleKey", new KeyboardShortcut(KeyCode.F8),
                "Key that toggles AutoPlay at runtime.");

            SearchNodeBudget = file.Bind("Solver", "SearchNodeBudget", 250000,
                "Maximum number of search nodes per decision. Higher is stronger but slower.");

            SearchTimeMs = file.Bind("Solver", "SearchTimeMs", 200,
                "Maximum search time per decision, in milliseconds.");
        }
    }
}
