using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BlackJacket.OptimalPlay
{
    /// <summary>
    /// Drives the player's draw phase with the perfect-information native solver. Searches
    /// run on <see cref="SolverWorker"/>; this component only captures positions on the Unity
    /// main thread, submits jobs and executes the returned move once the job completes.
    /// </summary>
    internal sealed class AutoPilot : MonoBehaviour
    {
        internal static bool PlayerInputActive;

        private readonly AutoSelector _selector = new AutoSelector();
        private float _cooldown;
        private string _status = "";
        private TextMeshProUGUI _statusText;
        private Canvas _statusCanvas;
        private int _errorCount;
        private bool _overlayFailed;

        // In-flight search for the current turn. The captured position is dropped whenever
        // the turn changes while the search runs, so a stale plan is never executed.
        private SolverJob<SolveResult> _decisionJob;
        private SolverState _decisionState;
        private int _decisionRevision;
        private object _decisionMatch;

        // In-flight search behind the F9 diagnostics dump.
        private SolverJob<SolveResult> _dumpJob;
        private SolverState _dumpState;

        private void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                if (_errorCount++ < 5)
                {
                    OptimalPlayPlugin.Log.LogError(e);
                }
            }
        }

        private void Tick()
        {
            Settings cfg = OptimalPlayPlugin.Cfg;
            if (cfg == null || !cfg.Enabled.Value)
            {
                CancelDecision();
                SetStatus("");
                return;
            }

            if (cfg.ToggleKey.Value.IsDown())
            {
                cfg.AutoPlay.Value = !cfg.AutoPlay.Value;
                OptimalPlayPlugin.Log.LogInfo($"AutoPlay {(cfg.AutoPlay.Value ? "enabled" : "disabled")}.");
            }

            _cooldown -= Time.unscaledDeltaTime;

            GameController gc = GameController.Instance;
            if (gc == null || gc.UI == null || gc.State == null
                || gc.State.Player == null || gc.State.Opponent == null)
            {
                CancelDecision();
                SetStatus("");
                return;
            }

            PollDump();

            if (cfg.VerboseKey.Value.IsDown())
            {
                cfg.LogState.Value = !cfg.LogState.Value;
                OptimalPlayPlugin.Log.LogInfo($"State dumps {(cfg.LogState.Value ? "enabled" : "disabled")}.");
            }

            if (cfg.DumpStateKey.Value.IsDown())
            {
                if (gc.CurrentMatch != null && gc.State.Player.DrawPile != null)
                {
                    DumpState(gc, "F9 dump");
                }
                else
                {
                    OptimalPlayPlugin.Log.LogInfo("State dump skipped: no match in progress.");
                }
            }

            if (OptionalCardActivation.IsActive)
            {
                SetStatus("AUTO: choosing optional card effect");
                if (cfg.AutoPlay.Value && _cooldown <= 0f)
                {
                    if (cfg.AutoActivateOptionalEffects.Value)
                    {
                        OptionalCardActivation.Activate();
                        OptimalPlayPlugin.Log.LogInfo("Optional card effect: activate.");
                    }
                    else
                    {
                        OptionalCardActivation.Skip();
                        OptimalPlayPlugin.Log.LogInfo("Optional card effect: skip.");
                    }
                    _cooldown = Delay(cfg);
                }
                return;
            }

            if (_selector.Tick(gc, cfg))
            {
                SetStatus(_selector.Status);
                return;
            }

            if (!cfg.AutoPlay.Value)
            {
                CancelDecision();
                SetStatus("AUTO-PLAY: off");
                return;
            }

            if (!Progression.HasPlayedTutorial)
            {
                CancelDecision();
                SetStatus("AUTO-PLAY: waiting (tutorial)");
                return;
            }

            if (gc.CurrentMatch == null)
            {
                CancelDecision();
                SetStatus("");
                return;
            }

            PlayerState ps = gc.State.Player;

            if (!IsPlayerTurn(gc, ps))
            {
                CancelDecision();
                SetStatus("");
                return;
            }

            if (!NativeSolver.Available)
            {
                CancelDecision();
                SetStatus("AUTO: native solver unavailable (see log)");
                return;
            }

            if (_decisionJob != null)
            {
                if (!_decisionJob.Done)
                {
                    SetStatus("AUTO: thinking...");
                    return;
                }
                CompleteDecision(gc, cfg, ps);
                return;
            }

            if (_cooldown > 0f)
            {
                return;
            }

            // If the draw pile is empty but the discard pile is not, the game is about to
            // shuffle a new pile. Wait for it instead of deciding on a stale deck.
            if (ps.DrawPile.CardAmount == 0 && ps.DiscardPile.CardAmount > 0)
            {
                SetStatus("AUTO: waiting for reshuffle");
                return;
            }

            StartDecision(cfg, gc, ps);
        }

        private static bool IsPlayerTurn(GameController gc, PlayerState ps)
        {
            bool inputActive = PlayerInputPatches.Patched
                ? PlayerInputActive
                : ps.DrawPile.InteractionsEnabled;
            if (!inputActive)
            {
                return false;
            }
            if (ps.PassedOnDrawing || ps.SkipTurns > 0)
            {
                return false;
            }
            if (CardPlacementController.IsHoldingCard)
            {
                return false;
            }
            Button pass = gc.UI.GetButton("pass");
            if (pass == null || !pass.gameObject.activeInHierarchy || !pass.interactable)
            {
                return false;
            }
            return true;
        }

        private static float Delay(Settings cfg)
        {
            return Mathf.Max(0.05f, cfg.ActionDelay.Value);
        }

        // ---------------------------------------------------------------- decision

        private void StartDecision(Settings cfg, GameController gc, PlayerState ps)
        {
            _decisionState = BuildSim(gc, ps);
            _decisionRevision = Revision(gc, ps);
            _decisionMatch = gc.CurrentMatch;
            SubmitDecision(cfg);
        }

        private void SubmitDecision(Settings cfg)
        {
            SolverState state = _decisionState;
            int nodes = Mathf.Max(1000, cfg.SearchNodeBudget.Value);
            int timeMs = Mathf.Max(20, cfg.SearchTimeMs.Value);
            _decisionJob = SolverWorker.Post(() => NativeSolver.Solve(state, nodes, timeMs));
        }

        private void CompleteDecision(GameController gc, Settings cfg, PlayerState ps)
        {
            SolverJob<SolveResult> job = _decisionJob;
            _decisionJob = null;
            SolverState sim = _decisionState;
            _decisionState = null;

            if (job.Error != null)
            {
                OptimalPlayPlugin.Log.LogError($"Solver failed: {job.Error.Message}");
                _cooldown = Delay(cfg);
                return;
            }

            // The player may have acted manually while the search ran; never execute a plan
            // that was computed for a different position.
            if (sim == null || !ReferenceEquals(_decisionMatch, gc.CurrentMatch) || Revision(gc, ps) != _decisionRevision)
            {
                return;
            }

            SolveResult result = job.Result;

            // The game cannot pay for a sleeve draw after all: solve again without it.
            if (result.Best.Kind == MoveKind.SleeveTop && !gc.PlayerCanDrawToSleeve())
            {
                sim.SleeveSize = 0;
                sim.Payable = 0;
                _decisionState = sim;
                _decisionRevision = Revision(gc, ps);
                SubmitDecision(cfg);
                return;
            }

            if (cfg.LogDecisions.Value)
            {
                var sb = new StringBuilder();
                sb.Append("Decision: ").Append(SolverReport.MoveLabel(result.Best, sim));
                sb.Append(" | P ").Append(result.PValue).Append(" vs O ").Append(result.OValue);
                if (!float.IsNaN(result.Value))
                {
                    sb.Append(" | ev ").Append(SolverReport.Format(result.Value));
                }
                sb.Append(" | ").Append(result.Nodes).Append(" nodes");
                if (result.Aborted)
                {
                    sb.Append(" (budget reached, greedy fallback)");
                }
                if (result.ProgressTieBreak)
                {
                    sb.Append(" [").Append(SolverReport.ProgressNote).Append(']');
                }
                var ranked = new List<MoveEvaluation>(result.Evaluations);
                ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
                sb.Append(" | options: ");
                for (int i = 0; i < ranked.Count && i < 4; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }
                    sb.Append(SolverReport.MoveLabel(ranked[i].Move, sim))
                        .Append('=').Append(SolverReport.Format(ranked[i].Value));
                }
                OptimalPlayPlugin.Log.LogInfo(sb.ToString());
            }

            if (cfg.LogState.Value)
            {
                var sb = new StringBuilder();
                AppendReport(sb, "decision", sim, result);
                OptimalPlayPlugin.Log.LogInfo(sb.ToString());
            }

            SetStatus($"AUTO: {SolverReport.MoveLabel(result.Best, sim)}  (P {ps.TableValue} vs O {gc.State.Opponent.TableValue})");
            ExecuteMove(gc, sim, result.Best);
            _cooldown = Delay(cfg);
        }

        private void CancelDecision()
        {
            _decisionJob = null;
            _decisionState = null;
        }

        /// <summary>
        /// Cheap fingerprint of the position the search was started from. If any of these
        /// change while a search runs, the answer is dropped and a fresh one is requested.
        /// </summary>
        private static int Revision(GameController gc, PlayerState ps)
        {
            PlayerState os = gc.State.Opponent;
            unchecked
            {
                int hash = ps.DrawPile.CardAmount;
                hash = (hash * 397) ^ ps.DiscardPile.CardAmount;
                hash = (hash * 397) ^ ps.TableDropArea.Cards.Length;
                hash = (hash * 397) ^ os.TableDropArea.Cards.Length;
                hash = (hash * 397) ^ ps.Bet.CoinValue;
                hash = (hash * 397) ^ (ps.PassedOnDrawing ? 1 : 0);
                hash = (hash * 397) ^ (ps.SkipTurns > 0 ? 1 : 0);
                hash = (hash * 397) ^ gc.UI.Sleeve.GetCards().Length;
                return hash;
            }
        }

        /// <summary>Log the current position and what the solver would do, without acting.</summary>
        private void DumpState(GameController gc, string source)
        {
            PollDump();
            if (_dumpJob != null)
            {
                OptimalPlayPlugin.Log.LogInfo("State dump skipped: a previous dump is still running.");
                return;
            }

            Settings cfg = OptimalPlayPlugin.Cfg;
            _dumpState = BuildSim(gc, gc.State.Player);
            SolverState state = _dumpState;
            int nodes = Mathf.Max(1000, cfg.SearchNodeBudget.Value);
            int timeMs = Mathf.Max(20, cfg.SearchTimeMs.Value);
            _dumpJob = SolverWorker.Post(() => NativeSolver.Solve(state, nodes, timeMs));
            OptimalPlayPlugin.Log.LogInfo($"{source}: searching in the background...");
        }

        private void PollDump()
        {
            if (_dumpJob == null || !_dumpJob.Done)
            {
                return;
            }

            SolverJob<SolveResult> job = _dumpJob;
            _dumpJob = null;
            SolverState sim = _dumpState;
            _dumpState = null;

            if (job.Error != null)
            {
                OptimalPlayPlugin.Log.LogError($"State dump failed: {job.Error.Message}");
                return;
            }

            var sb = new StringBuilder();
            AppendReport(sb, "dump", sim, job.Result);
            OptimalPlayPlugin.Log.LogInfo(sb.ToString());
        }

        private static void AppendReport(StringBuilder sb, string source, SolverState sim, SolveResult result)
        {
            sb.Append("=== Optimal Play: ").Append(source).AppendLine(" ===");
            sb.Append("match: target ").Append(sim.PlainTarget)
                .Append(", holds at ").Append(sim.HoldsAt)
                .Append(", rules: ").Append(Rules(sim)).AppendLine();
            AppendSide(sb, "player", sim, sim.P, result.PValue, result.PTarget);
            AppendSide(sb, "opponent", sim, sim.O, result.OValue, result.OTarget);
            sb.AppendLine("moves:");
            sb.Append(SolverReport.DescribeMoves(sim, result));
            sb.Append("result: ").Append(SolverReport.MoveLabel(result.Best, sim));
            if (!float.IsNaN(result.Value))
            {
                sb.Append(" | ev ").Append(SolverReport.Format(result.Value));
            }
            sb.Append(" | ").Append(result.Nodes).Append(" nodes");
            sb.Append(result.Aborted ? " (budget reached, greedy fallback)" : " (complete)");
            if (result.ProgressTieBreak)
            {
                sb.Append(" [").Append(SolverReport.ProgressNote).Append(']');
            }
            sb.AppendLine();
        }

        private static string Rules(SolverState sim)
        {
            var rules = new List<string>();
            if (sim.Uprising)
            {
                rules.Add("Uprising");
            }
            if (sim.Supper)
            {
                rules.Add("Supper");
            }
            return rules.Count == 0 ? "none" : string.Join(", ", rules);
        }

        private static void AppendSide(StringBuilder sb, string label, SolverState sim, SolverSide side,
            int value, int target)
        {
            sb.Append(label).Append(": value ").Append(value)
                .Append(", target ").Append(target)
                .Append(", capacity ").Append(side.Capacity)
                .Append(", passed ").Append(side.Passed ? "yes" : "no")
                .Append(", bet ").Append(side.Bet);
            if (ReferenceEquals(side, sim.P))
            {
                sb.Append(", sleeve draws ").Append(side.SleeveDraws).Append('/').Append(sim.SleeveSize)
                    .Append(", next sleeve cost ").Append(NextSleeveCost(sim, side))
                    .Append(", payable ").Append(sim.Payable);
            }
            else
            {
                sb.Append(", insight ").Append(sim.InsightLeft);
            }
            sb.AppendLine();
            sb.Append("  table: ").AppendLine(CardList(side.Table, int.MaxValue));
            sb.Append("  deck top: ").AppendLine(CardList(side.Deck, side.DeckPos, 6));
            sb.Append("  discard: ").Append(side.DiscardCount).AppendLine(" cards");
            sb.Append("  sleeve: ").AppendLine(CardList(side.Sleeve, int.MaxValue));
        }

        private static string NextSleeveCost(SolverState sim, SolverSide side)
        {
            if (sim.SleeveCosts == null || sim.SleeveCosts.Length == 0)
            {
                return "n/a";
            }
            return sim.SleeveCosts[Math.Min(sim.SleeveCosts.Length - 1, side.SleeveDraws)].ToString();
        }

        private static string CardList(List<SolverCard> cards, int max)
        {
            return CardList(cards, 0, max);
        }

        private static string CardList(List<SolverCard> cards, int start, int max)
        {
            if (cards.Count <= start)
            {
                return "(empty)";
            }
            var parts = new List<string>();
            for (int i = start; i < cards.Count && parts.Count < max; i++)
            {
                SolverCard card = cards[i];
                string part = card.Label;
                if (card.Effect != null)
                {
                    part += " (" + card.Effect + ")";
                }
                parts.Add(part);
            }
            if (cards.Count - start > max)
            {
                parts.Add("+" + (cards.Count - start - max) + " more");
            }
            return string.Join(", ", parts);
        }

        internal static SolverState Capture(GameController gc)
        {
            return BuildSim(gc, gc.State.Player);
        }

        private static SolverState BuildSim(GameController gc, PlayerState ps)
        {
            PlayerState os = gc.State.Opponent;
            var sim = new SolverState
            {
                PlainTarget = CardGame.TableTargetValue,
                PMods = ps.TableValueModifiers.Sum(),
                OMods = os.TableValueModifiers.Sum(),
                HoldsAt = gc.CurrentMatch != null ? gc.CurrentMatch.OpponentHoldsAtXTableValue : 17,
                InsightLeft = OpponentController.Instance != null ? OpponentController.Instance.DeckInsight : 0,
                SleeveSize = GameController.Config.SleeveSize.ModifiedValue,
                SleeveCosts = (int[])GameController.Config.DrawToSleeveCosts.ModifiedValue.Clone(),
                Uprising = gc.CurrentMatch != null && gc.CurrentMatch.HasRule(GameRule.Uprising),
                Supper = gc.CurrentMatch != null && gc.CurrentMatch.HasRule(GameRule.Supper),
            };

            int stashValue = ps.Stash.CoinValue;
            int potValue = gc.UI.WinnersPot.CoinValue;
            bool potUsable = potValue <= 0 || ps.CoinManager.CanPay(stashValue + 1, true);
            sim.Payable = stashValue + (potUsable ? potValue : 0);

            sim.P = BuildSide(ps.DrawPile.Cards, ps.DiscardPile.Cards.Length, gc.UI.Sleeve.GetCards(),
                ps.TableDropArea.Cards, ps.TableDropArea.FreeSlotCount, ps.Bet.CoinValue,
                ps.DrawToSleeveCount, ps.PassedOnDrawing);
            sim.O = BuildSide(os.DrawPile.Cards, os.DiscardPile.Cards.Length, null,
                os.TableDropArea.Cards, os.TableDropArea.FreeSlotCount, os.Bet.CoinValue,
                0, os.PassedOnDrawing);
            return sim;
        }

        private static SolverSide BuildSide(Card3D[] drawPile, int discardCount, Card3D[] sleeve,
            Card3D[] table, int freeSlots, int bet, int sleeveDraws, bool passed)
        {
            var deck = new List<SolverCard>(drawPile.Length);
            for (int i = drawPile.Length - 1; i >= 0; i--)
            {
                deck.Add(SolverCard.From(drawPile[i]));
            }

            var side = new SolverSide
            {
                Deck = deck,
                DiscardCount = discardCount,
                DeckPos = 0,
                Capacity = freeSlots,
                Bet = bet,
                SleeveDraws = sleeveDraws,
                Passed = passed,
            };

            if (sleeve != null)
            {
                foreach (Card3D card in sleeve)
                {
                    side.Sleeve.Add(SolverCard.From(card));
                }
            }
            foreach (Card3D card in table)
            {
                side.Table.Add(SolverCard.From(card));
            }
            return side;
        }

        // ---------------------------------------------------------------- execution

        private void ExecuteMove(GameController gc, SolverState sim, SolverMove move)
        {
            GameUI ui = gc.UI;

            switch (move.Kind)
            {
                case MoveKind.Pass:
                    PressPass(gc);
                    return;

                case MoveKind.PlayTop:
                {
                    if (!ui.Player.DrawPile.TryToDraw(out Card3D card))
                    {
                        PressPass(gc);
                        return;
                    }
                    CardPlacementController.PickupCard(card, ui.Player.DrawPile, InputType.KeyboardAndMouse);
                    if (!DropToTable(card, move.ToOpponent))
                    {
                        CardPlacementController.ReturnCard();
                        PressPass(gc);
                    }
                    return;
                }

                case MoveKind.SleeveTop:
                {
                    if (!gc.PlayerCanDrawToSleeve())
                    {
                        PressPass(gc);
                        return;
                    }
                    if (!ui.Player.DrawPile.TryToDraw(out Card3D card))
                    {
                        PressPass(gc);
                        return;
                    }
                    CardPlacementController.PickupCard(card, ui.Player.DrawPile, InputType.KeyboardAndMouse);
                    if (!DropToSleeve(gc, card))
                    {
                        CardPlacementController.ReturnCard();
                        PressPass(gc);
                    }
                    return;
                }

                case MoveKind.PlaySleeve:
                {
                    Card3D[] sleeve = ui.Sleeve.GetCards();
                    if (move.SleeveIndex < 0 || move.SleeveIndex >= sleeve.Length)
                    {
                        PressPass(gc);
                        return;
                    }
                    Card3D card = sleeve[move.SleeveIndex];
                    if (!ui.Sleeve.TryPickupCard(card))
                    {
                        PressPass(gc);
                        return;
                    }
                    CardPlacementController.PickupCard(card, ui.Sleeve, InputType.KeyboardAndMouse);
                    if (!DropToTable(card, move.ToOpponent))
                    {
                        CardPlacementController.ReturnCard();
                        PressPass(gc);
                    }
                    return;
                }
            }
        }

        private static bool DropToTable(Card3D card, bool toOpponent)
        {
            List<SingleCardField> areas = toOpponent
                ? CardPlacementController.FindAvailableOpponentDropAreas(card)
                : CardPlacementController.FindAvailablePlayerDropAreas(card);
            if (!toOpponent && areas.Count == 0)
            {
                areas = CardPlacementController.FindAvailableOpponentDropAreas(card);
            }
            if (areas.Count == 0)
            {
                return false;
            }

            SingleCardField slot = null;
            int bestScore = int.MaxValue;
            foreach (SingleCardField area in areas)
            {
                int cost = 0;
                CardSlotEffectPlayer effect = area.GetComponent<CardSlotEffectPlayer>();
                if (effect != null)
                {
                    cost = effect.CostToActivate;
                }
                int score = cost * 10 + (area.Card == null ? 0 : 1);
                if (score < bestScore)
                {
                    bestScore = score;
                    slot = area;
                }
            }

            CardPlacementController.SetCurrentDropArea(slot);
            return CardPlacementController.TryToDrop();
        }

        private static bool DropToSleeve(GameController gc, Card3D card)
        {
            SleeveUI sleeve = gc.UI.Sleeve;
            if (!sleeve.CanDrop(card))
            {
                return false;
            }
            CardPlacementController.SetCurrentDropArea(sleeve);
            sleeve.EnableCardDropHint(card, true);
            return CardPlacementController.TryToDrop();
        }

        private static void PressPass(GameController gc)
        {
            Button pass = gc.UI.GetButton("pass");
            if (pass != null && pass.gameObject.activeInHierarchy && pass.interactable)
            {
                pass.onClick.Invoke();
            }
        }

        // ---------------------------------------------------------------- status overlay

        private void SetStatus(string text)
        {
            _status = text;
            Settings cfg = OptimalPlayPlugin.Cfg;
            if (cfg == null || !cfg.ShowStatus.Value || string.IsNullOrEmpty(text))
            {
                if (_statusCanvas != null && _statusCanvas.gameObject.activeSelf)
                {
                    _statusCanvas.gameObject.SetActive(false);
                }
                return;
            }

            EnsureOverlay();
            if (_statusCanvas == null || _statusText == null)
            {
                return;
            }
            if (!_statusCanvas.gameObject.activeSelf)
            {
                _statusCanvas.gameObject.SetActive(true);
            }
            if (_statusText.text != text)
            {
                _statusText.text = text;
            }
        }

        private void EnsureOverlay()
        {
            if (_statusCanvas != null || _overlayFailed)
            {
                return;
            }
            try
            {
                var canvasGo = new GameObject("OptimalPlayStatus");
                canvasGo.transform.SetParent(transform, false);
                _statusCanvas = canvasGo.AddComponent<Canvas>();
                _statusCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _statusCanvas.sortingOrder = 32000;

                var textGo = new GameObject("Text");
                textGo.transform.SetParent(canvasGo.transform, false);
                _statusText = textGo.AddComponent<TextMeshProUGUI>();
                _statusText.fontSize = 20f;
                _statusText.color = Color.white;
                _statusText.alignment = TextAlignmentOptions.TopLeft;
                _statusText.raycastTarget = false;

                RectTransform rect = _statusText.rectTransform;
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(14f, -14f);
                rect.sizeDelta = new Vector2(900f, 40f);

                var shadow = textGo.AddComponent<Shadow>();
                shadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
                shadow.effectDistance = new Vector2(1f, -1f);
            }
            catch (Exception e)
            {
                _overlayFailed = true;
                OptimalPlayPlugin.Log.LogWarning($"Status overlay disabled: {e.Message}");
            }
        }
    }
}
