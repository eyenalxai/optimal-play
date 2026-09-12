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
    /// Drives the player's draw phase with the perfect-information solver.
    /// </summary>
    internal sealed class AutoPilot : MonoBehaviour
    {
        internal static bool PlayerInputActive;

        private float _cooldown;
        private string _status = "";
        private TextMeshProUGUI _statusText;
        private Canvas _statusCanvas;
        private int _errorCount;
        private bool _overlayFailed;

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
                SetStatus("");
                return;
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

            if (!cfg.AutoPlay.Value)
            {
                SetStatus("AUTO-PLAY: off");
                return;
            }

            if (!Progression.HasPlayedTutorial)
            {
                SetStatus("AUTO-PLAY: waiting (tutorial)");
                return;
            }

            if (gc.CurrentMatch == null)
            {
                SetStatus("");
                return;
            }

            PlayerState ps = gc.State.Player;

            if (!IsPlayerTurn(gc, ps))
            {
                SetStatus("");
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

            _cooldown = Delay(cfg);
            DecideAndPlay(gc, ps);
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

        private void DecideAndPlay(GameController gc, PlayerState ps)
        {
            Settings cfg = OptimalPlayPlugin.Cfg;
            SolverState sim = BuildSim(gc, ps);

            var solver = new RoundSolver
            {
                NodeBudget = Mathf.Max(1000, cfg.SearchNodeBudget.Value),
                TimeBudgetMs = Mathf.Max(20, cfg.SearchTimeMs.Value),
            };

            SolverMove move = solver.BestMove(sim, out float value, out int nodes, out bool aborted,
                out List<MoveEvaluation> evaluations);

            // If the game cannot pay for a sleeve draw after all, solve again without it.
            if (move.Kind == MoveKind.SleeveTop && !gc.PlayerCanDrawToSleeve())
            {
                sim.SleeveSize = 0;
                sim.Payable = 0;
                move = solver.BestMove(sim, out value, out nodes, out aborted, out evaluations);
            }

            if (cfg.LogDecisions.Value)
            {
                var sb = new StringBuilder();
                sb.Append("Decision: ").Append(Describe(move, sim));
                sb.Append(" | P ").Append(sim.PValue).Append(" vs O ").Append(sim.OValue);
                if (!float.IsNaN(value))
                {
                    sb.Append(" | ev ").Append(value.ToString("+0.##;-0.##;0"));
                }
                sb.Append(" | ").Append(nodes).Append(" nodes");
                if (aborted)
                {
                    sb.Append(" (budget reached, greedy fallback)");
                }
                sb.Append(" | options: ");
                sb.Append(string.Join(", ",
                    evaluations.OrderByDescending(e => e.Value).Take(4)
                        .Select(e => $"{Describe(e.Move, sim)}={e.Value:+0.##;-0.##;0}")));
                OptimalPlayPlugin.Log.LogInfo(sb.ToString());
            }

            SetStatus($"AUTO: {Describe(move, sim)}  (P {ps.TableValue} vs O {gc.State.Opponent.TableValue})");
            ExecuteMove(gc, sim, move);
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
                SleeveCosts = GameController.Config.DrawToSleeveCosts.ModifiedValue,
                Uprising = gc.CurrentMatch != null && gc.CurrentMatch.HasRule(GameRule.Uprising),
                Supper = gc.CurrentMatch != null && gc.CurrentMatch.HasRule(GameRule.Supper),
            };

            int stashValue = ps.Stash.CoinValue;
            int potValue = gc.UI.WinnersPot.CoinValue;
            bool potUsable = potValue <= 0 || ps.CoinManager.CanPay(stashValue + 1, true);
            sim.Payable = stashValue + (potUsable ? potValue : 0);

            sim.P = BuildSide(ps.DrawPile.Cards, ps.DiscardPile.Cards, gc.UI.Sleeve.GetCards(),
                ps.TableDropArea.Cards, ps.TableDropArea.FreeSlotCount, ps.Bet.CoinValue,
                ps.DrawToSleeveCount, ps.PassedOnDrawing);
            sim.O = BuildSide(os.DrawPile.Cards, os.DiscardPile.Cards, null,
                os.TableDropArea.Cards, os.TableDropArea.FreeSlotCount, os.Bet.CoinValue,
                0, os.PassedOnDrawing);
            return sim;
        }

        private static SolverSide BuildSide(Card3D[] drawPile, Card3D[] discardPile, Card3D[] sleeve,
            Card3D[] table, int freeSlots, int bet, int sleeveDraws, bool passed)
        {
            var deck = new List<SolverCard>(drawPile.Length);
            for (int i = drawPile.Length - 1; i >= 0; i--)
            {
                deck.Add(SolverCard.From(drawPile[i]));
            }

            var discard = new List<SolverCard>(discardPile.Length);
            foreach (Card3D card in discardPile)
            {
                discard.Add(SolverCard.From(card));
            }

            var side = new SolverSide
            {
                Deck = deck,
                Discard = discard,
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

        private static string Describe(SolverMove move, SolverState sim)
        {
            switch (move.Kind)
            {
                case MoveKind.PlayTop:
                    return $"play top card [{Values(sim.P.Top)}]";
                case MoveKind.SleeveTop:
                    return $"sleeve top card [{Values(sim.P.Top)}]";
                case MoveKind.PlaySleeve:
                    return move.SleeveIndex >= 0 && move.SleeveIndex < sim.P.Sleeve.Count
                        ? $"play sleeve card [{Values(sim.P.Sleeve[move.SleeveIndex])}]"
                        : "play sleeve card";
                default:
                    return "pass";
            }
        }

        private static string Values(SolverCard card)
        {
            return card == null ? "?" : string.Join("/", card.Values.Select(v => v.ToString()));
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
                    if (!DropToTable(card))
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
                        ExecuteFallback(gc, sim);
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
                    if (!DropToTable(card))
                    {
                        CardPlacementController.ReturnCard();
                        PressPass(gc);
                    }
                    return;
                }
            }
        }

        private void ExecuteFallback(GameController gc, SolverState sim)
        {
            SolverState copy = sim.Clone();
            copy.SleeveSize = 0;
            copy.Payable = 0;
            var solver = new RoundSolver();
            SolverMove move = solver.BestMove(copy, out _, out _, out _, out _);
            if (move.Kind == MoveKind.SleeveTop)
            {
                move = new SolverMove { Kind = MoveKind.Pass };
            }
            ExecuteMove(gc, copy, move);
        }

        private static bool DropToTable(Card3D card)
        {
            List<SingleCardField> areas = CardPlacementController.FindAvailablePlayerDropAreas(card);
            if (areas.Count == 0)
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
            if (_statusCanvas == null)
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
