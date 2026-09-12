using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace BlackJacket.OptimalPlay
{
    /// <summary>
    /// Automates the blocking choice dialogs of a match with the same perfect-information
    /// search that plays the draw phase:
    ///
    /// - insight: reorders the revealed top cards of either deck to the arrangement with the
    ///   best solver outcome,
    /// - demand: takes the revealed card (into the sleeve) with the best solver outcome, or
    ///   skips when nothing helps,
    /// - card choice: picks the table card with the best solver outcome when an effect such
    ///   as the awakened Greed 3 forces the player to sleeve one of their slots,
    /// - shuffle: picks the deck to shuffle by comparing sampled outcomes of both choices.
    /// </summary>
    internal sealed class AutoSelector
    {
        private const float SettleSeconds = 1.25f;
        private const float TotalInsightSeconds = 1.5f;
        private const float TotalChoiceSeconds = 2.5f;
        private const int MaxInsightCandidates = 160;
        private const int ShuffleSamples = 4;

        private sealed class InsightCandidate
        {
            public List<SolverCard> Perm;
            public int Split;
            public float Value;
        }

        private float _openedAt = -1f;
        private bool _handled;
        private bool _shuffleDecided;
        private bool _shuffleOwnDeck;
        private Card3D _choiceCard;
        private float _choiceStartedAt = -1f;
        private string _status = "";
        private int _nodeBudget = 8000;
        private int _timeMs = 10;

        public string Status => _status;

        /// <summary>Handles any active selection dialog. Returns true while a dialog owns the screen.</summary>
        public bool Tick(GameController gc, Settings cfg)
        {
            _status = "";
            if (gc == null || gc.UI == null || gc.State == null)
            {
                return false;
            }
            if (!Progression.HasPlayedTutorial)
            {
                return false;
            }

            _nodeBudget = Math.Max(2000, cfg.SearchNodeBudget.Value / 10);
            _timeMs = Math.Max(5, cfg.SearchTimeMs.Value / 10);

            if (IsActive(gc.UI.InsightDialog))
            {
                HandleInsight(gc, cfg, gc.UI.InsightDialog);
                return true;
            }
            if (IsActive(gc.UI.InsightOnOpponentDialog))
            {
                HandleInsight(gc, cfg, gc.UI.InsightOnOpponentDialog);
                return true;
            }
            if (IsActive(gc.UI.DemandDialog))
            {
                HandleDemand(gc, cfg, gc.UI.DemandDialog);
                return true;
            }
            if (ActiveCardChoice.IsActive)
            {
                HandleCardChoice(gc, cfg);
                return true;
            }

            ResetDialog();
            HandleShuffleChoice(gc, cfg);
            return false;
        }

        private static bool IsActive(InsightDialog dialog)
        {
            return dialog != null && dialog.IsActive;
        }

        private void TrackOpen()
        {
            if (_openedAt < 0f)
            {
                _openedAt = Time.realtimeSinceStartup;
                _handled = false;
            }
        }

        private void ResetDialog()
        {
            _openedAt = -1f;
            _handled = false;
            _choiceCard = null;
            _choiceStartedAt = -1f;
        }

        // ---------------------------------------------------------------- insight

        private void HandleInsight(GameController gc, Settings cfg, InsightDialog dialog)
        {
            TrackOpen();
            if (!cfg.AutoSelectInsight.Value)
            {
                _status = "insight: manual";
                return;
            }
            if (Time.realtimeSinceStartup - _openedAt < SettleSeconds)
            {
                return;
            }

            ReorderCardLine line = dialog.ReorderCardLine;
            if (line == null)
            {
                return;
            }

            if (!_handled)
            {
                _handled = true;
                try
                {
                    ApplyBestInsight(gc, dialog, line);
                }
                catch (Exception e)
                {
                    OptimalPlayPlugin.Log.LogWarning($"Insight automation failed: {e.Message}");
                }
            }

            PressInsightOk(gc, dialog);
        }

        private void ApplyBestInsight(GameController gc, InsightDialog dialog, ReorderCardLine line)
        {
            GameObject[] objects = line.GetObjects();
            var displayCards = new List<Card3D>();
            GameObject placeholder = null;
            foreach (GameObject go in objects)
            {
                if (go == null)
                {
                    continue;
                }
                Card3D card = go.GetComponent<Card3D>();
                if (card != null)
                {
                    displayCards.Add(card);
                }
                else if (placeholder == null)
                {
                    placeholder = go;
                }
            }

            int shownCount = displayCards.Count;
            if (shownCount <= 1)
            {
                return;
            }

            bool onOpponent = dialog.InsightOnOpponent;
            SolverState baseState = AutoPilot.Capture(gc);
            SolverSide side = onOpponent ? baseState.O : baseState.P;
            if (side.Deck.Count < shownCount)
            {
                return;
            }

            List<SolverCard> top = side.Deck.GetRange(0, shownCount);
            List<SolverCard> rest = side.Deck.GetRange(shownCount, side.Deck.Count - shownCount);

            var goByCard = new Dictionary<SolverCard, GameObject>();
            for (int i = 0; i < shownCount; i++)
            {
                // Display reads bottom-to-top; the deck list is top-first.
                goByCard[top[shownCount - 1 - i]] = displayCards[i].gameObject;
            }

            var candidates = new List<InsightCandidate>();
            var seen = new HashSet<string>();
            foreach (List<SolverCard> perm in InsightPlanner.Permutations(top))
            {
                for (int split = 0; split <= shownCount; split++)
                {
                    List<SolverCard> order = InsightPlanner.CandidateOrder(perm, split, rest);
                    if (!seen.Add(InsightPlanner.OrderKey(order)))
                    {
                        continue;
                    }
                    candidates.Add(new InsightCandidate { Perm = perm, Split = split });
                    if (candidates.Count >= MaxInsightCandidates)
                    {
                        break;
                    }
                }
                if (candidates.Count >= MaxInsightCandidates)
                {
                    break;
                }
            }
            if (candidates.Count == 0)
            {
                return;
            }

            float deadline = Time.realtimeSinceStartup + TotalInsightSeconds;
            float bestValue = float.NegativeInfinity;
            InsightCandidate best = null;
            int evaluated = 0;
            foreach (InsightCandidate candidate in candidates)
            {
                if (best != null && Time.realtimeSinceStartup > deadline)
                {
                    break;
                }
                SolverState clone = baseState.Clone();
                SolverSide cloneSide = onOpponent ? clone.O : clone.P;
                cloneSide.Deck = InsightPlanner.CandidateOrder(candidate.Perm, candidate.Split, rest);
                candidate.Value = Evaluate(clone);
                evaluated++;
                if (candidate.Value > bestValue)
                {
                    bestValue = candidate.Value;
                    best = candidate;
                }
            }
            if (best == null)
            {
                return;
            }

            var target = new List<GameObject>(objects.Length);
            for (int i = shownCount - 1; i >= best.Split; i--)
            {
                target.Add(goByCard[best.Perm[i]]);
            }
            if (placeholder != null)
            {
                target.Add(placeholder);
            }
            for (int i = best.Split - 1; i >= 0; i--)
            {
                target.Add(goByCard[best.Perm[i]]);
            }

            List<GameObject> list = line.ActiveObjects;
            for (int i = 0; i < target.Count && i < list.Count; i++)
            {
                GameObject want = target[i];
                if (list[i] == want)
                {
                    continue;
                }
                list.Remove(want);
                list.Insert(i, want);
            }
            line.UpdatePositions();

            OptimalPlayPlugin.Log.LogInfo(
                $"Insight: arranged top {shownCount} cards of the {(onOpponent ? "opponent" : "player")} deck, "
                + $"value {Format(bestValue)} over {evaluated}/{candidates.Count} candidates.");
            _status = $"insight: reorder (ev {Format(bestValue)})";
        }

        // ---------------------------------------------------------------- demand

        private void HandleDemand(GameController gc, Settings cfg, InsightDialog dialog)
        {
            TrackOpen();
            if (!cfg.AutoSelectDemand.Value)
            {
                _status = "demand: manual";
                return;
            }
            if (Time.realtimeSinceStartup - _openedAt < SettleSeconds)
            {
                return;
            }

            SelectCardLine line = dialog.SelectCardLine;
            if (line == null)
            {
                return;
            }

            if (!_handled)
            {
                _handled = true;
                try
                {
                    ApplyBestDemand(gc, line);
                }
                catch (Exception e)
                {
                    OptimalPlayPlugin.Log.LogWarning($"Demand automation failed: {e.Message}");
                }
            }

            if (line.SelectedCard == null)
            {
                PressDemandOk(gc);
            }
        }

        private void ApplyBestDemand(GameController gc, SelectCardLine line)
        {
            GameObject[] objects = line.GetObjects();
            var shown = new List<Card3D>();
            foreach (GameObject go in objects)
            {
                if (go == null)
                {
                    continue;
                }
                Card3D card = go.GetComponent<Card3D>();
                if (card != null)
                {
                    shown.Add(card);
                }
            }
            if (shown.Count == 0)
            {
                return;
            }

            PlayerState player = gc.State.Player;
            Card3D[] pile = player.DrawPile.Cards;
            SolverState baseState = AutoPilot.Capture(gc);
            float skipValue = Evaluate(baseState.Clone());

            var ranked = new List<(Card3D Card, float Value)>();
            foreach (Card3D card in shown)
            {
                int position = Array.IndexOf(pile, card);
                if (position < 0)
                {
                    continue;
                }
                int deckIndex = pile.Length - 1 - position;
                if (deckIndex < 0 || deckIndex >= baseState.P.Deck.Count)
                {
                    continue;
                }

                SolverState clone = baseState.Clone();
                clone.P.Deck = new List<SolverCard>(baseState.P.Deck);
                clone.P.Discard = new List<SolverCard>(baseState.P.Discard);
                SolverCard picked = clone.P.Deck[deckIndex];
                clone.P.Deck.RemoveAt(deckIndex);
                AddToSleeve(clone, picked);
                ranked.Add((card, Evaluate(clone)));
            }
            if (ranked.Count == 0)
            {
                return;
            }
            ranked.Sort((a, b) => b.Value.CompareTo(a.Value));

            foreach ((Card3D card, float value) in ranked)
            {
                if (value <= skipValue)
                {
                    break;
                }
                card.Interactions.EmitClicked();
                if (line.SelectedCard == card)
                {
                    OptimalPlayPlugin.Log.LogInfo(
                        $"Demand: taking [{Values(card)}] (value {Format(value)} vs skip {Format(skipValue)}).");
                    _status = $"demand: take [{Values(card)}] (ev {Format(value)})";
                    return;
                }
            }

            OptimalPlayPlugin.Log.LogInfo(
                $"Demand: skipping (best {Format(ranked[0].Value)} vs skip {Format(skipValue)}).");
            _status = "demand: skip";
        }

        private static void AddToSleeve(SolverState sim, SolverCard card)
        {
            if (sim.SleeveSize <= 0)
            {
                sim.P.Discard.Add(card);
                return;
            }
            int max = Math.Max(sim.SleeveSize, 1);
            while (sim.P.Sleeve.Count >= max)
            {
                sim.P.Sleeve.RemoveAt(0);
            }
            sim.P.Sleeve.Add(card);
        }

        // ---------------------------------------------------------------- table card choice

        /// <summary>
        /// Effects like the awakened Greed 3 ("Sleeve a card from your slots") block on a
        /// choice between the player's table cards. The move is forced, so sleeve the card
        /// whose removal leaves the best round outcome - usually the least useful one.
        /// </summary>
        private void HandleCardChoice(GameController gc, Settings cfg)
        {
            float started = CardChoicePatches.StartedAt;
            if (started <= 0f)
            {
                _status = "card choice: waiting for candidates";
                return;
            }
            if (started != _choiceStartedAt)
            {
                _choiceStartedAt = started;
                _choiceCard = null;
            }

            if (!cfg.AutoSelectCardChoice.Value)
            {
                _status = "card choice: manual";
                return;
            }
            if (Time.realtimeSinceStartup - started < SettleSeconds)
            {
                return;
            }

            Card3D[] choices = CardChoicePatches.Choices;
            if (choices == null || choices.Length == 0)
            {
                return;
            }

            if (_choiceCard == null)
            {
                try
                {
                    _choiceCard = ChooseCard(gc, choices);
                }
                catch (Exception e)
                {
                    OptimalPlayPlugin.Log.LogWarning($"Card choice failed ({e.Message}); taking the first candidate.");
                }
                if (_choiceCard == null)
                {
                    _choiceCard = choices[0];
                    _status = "card choice: fallback";
                }
            }

            if (_choiceCard != null)
            {
                ActiveCardChoice.SetSelectedCard(_choiceCard);
            }
        }

        private Card3D ChooseCard(GameController gc, Card3D[] choices)
        {
            SolverState baseState = AutoPilot.Capture(gc);
            Card3D[] table = gc.State.Player.TableDropArea.Cards;
            var solver = new RoundSolver
            {
                NodeBudget = Math.Max(1000, OptimalPlayPlugin.Cfg.SearchNodeBudget.Value),
                TimeBudgetMs = Math.Max(20, OptimalPlayPlugin.Cfg.SearchTimeMs.Value),
            };

            // Sleeving a dead card is nearly always right, so evaluate the likely best
            // candidates first; a timeout then cannot drop the best option.
            var ordered = choices
                .Where(c => c != null)
                .Select(c => new { Card = c, Sim = SolverCard.From(c) })
                .OrderBy(x => x.Sim.IsDead ? 0 : 1)
                .ThenBy(x => x.Sim.Highest)
                .ToArray();

            var ranked = new List<(Card3D Card, SolverCard Sim, float Value)>();
            float deadline = Time.realtimeSinceStartup + TotalChoiceSeconds;
            foreach (var candidate in ordered)
            {
                int index = Array.IndexOf(table, candidate.Card);
                if (index < 0 || index >= baseState.P.Table.Count)
                {
                    continue;
                }

                SolverState clone = baseState.Clone();
                clone.P.Discard = new List<SolverCard>(baseState.P.Discard);
                SolverCard moved = clone.P.Table[index];
                clone.P.Table.RemoveAt(index);
                if (!moved.IsHollow)
                {
                    // The vacated slot is usable again (a hollow card takes its own slot with it).
                    clone.P.Capacity++;
                }
                AddToSleeve(clone, moved);
                clone.InvalidateValues();

                ranked.Add((candidate.Card, moved, solver.Evaluate(clone)));
                if (Time.realtimeSinceStartup > deadline)
                {
                    break;
                }
            }
            if (ranked.Count == 0)
            {
                return null;
            }
            ranked.Sort((a, b) =>
            {
                int cmp = b.Value.CompareTo(a.Value);
                // Equal outcomes: keep the live cards and sleeve the dead one, so unmodeled
                // effects have the better material to work with.
                return cmp != 0 ? cmp : (a.Sim.IsDead ? 0 : 1).CompareTo(b.Sim.IsDead ? 0 : 1);
            });

            if (OptimalPlayPlugin.Cfg.LogDecisions.Value)
            {
                string source = CardChoicePatches.SourceCard != null
                    ? SolverCard.From(CardChoicePatches.SourceCard).Name
                    : "effect";
                string options = string.Join(", ", ranked.Select(r => $"{r.Sim.Label}={Format(r.Value)}"));
                OptimalPlayPlugin.Log.LogInfo(
                    $"Card choice ({source}): sleeving {ranked[0].Sim.Label} (value {Format(ranked[0].Value)}; "
                    + $"options: {options}).");
            }
            _status = $"card choice: sleeve {ranked[0].Sim.Label} (ev {Format(ranked[0].Value)})";
            return ranked[0].Card;
        }

        // ---------------------------------------------------------------- shuffle

        private void HandleShuffleChoice(GameController gc, Settings cfg)
        {
            Button playerButton = gc.UI.GetButton("select_deck_player");
            Button opponentButton = gc.UI.GetButton("select_deck_opponent");
            bool visible = playerButton != null && opponentButton != null
                && playerButton.gameObject.activeInHierarchy
                && opponentButton.gameObject.activeInHierarchy;
            if (!visible)
            {
                _shuffleDecided = false;
                return;
            }
            if (!cfg.AutoSelectShuffle.Value)
            {
                _status = "shuffle: manual";
                return;
            }

            if (!_shuffleDecided)
            {
                _shuffleDecided = true;
                try
                {
                    _shuffleOwnDeck = DecideShuffle(gc);
                }
                catch (Exception e)
                {
                    _shuffleOwnDeck = false;
                    OptimalPlayPlugin.Log.LogWarning($"Shuffle choice failed ({e.Message}); shuffling the opponent deck.");
                }
            }

            Button chosen = _shuffleOwnDeck ? playerButton : opponentButton;
            chosen.onClick.Invoke();
            _status = _shuffleOwnDeck ? "shuffle: own deck" : "shuffle: opponent deck";
        }

        private bool DecideShuffle(GameController gc)
        {
            SolverState sim = AutoPilot.Capture(gc);
            var rng = new System.Random(Environment.TickCount);
            float opponentSum = 0f;
            float playerSum = 0f;
            for (int i = 0; i < ShuffleSamples; i++)
            {
                SolverState opponentClone = sim.Clone();
                opponentClone.O.Deck = Shuffled(sim.O.Deck, rng);
                opponentSum += Evaluate(opponentClone);

                SolverState playerClone = sim.Clone();
                playerClone.P.Deck = Shuffled(sim.P.Deck, rng);
                playerSum += Evaluate(playerClone);
            }

            float opponentValue = opponentSum / ShuffleSamples;
            float playerValue = playerSum / ShuffleSamples;
            OptimalPlayPlugin.Log.LogInfo(
                $"Shuffle choice: own deck {Format(playerValue)} vs opponent deck {Format(opponentValue)} (sampled).");
            return playerValue > opponentValue;
        }

        private static List<SolverCard> Shuffled(List<SolverCard> deck, System.Random rng)
        {
            var copy = new List<SolverCard>(deck);
            for (int i = copy.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                SolverCard tmp = copy[i];
                copy[i] = copy[j];
                copy[j] = tmp;
            }
            return copy;
        }

        // ---------------------------------------------------------------- helpers

        private float Evaluate(SolverState state)
        {
            var solver = new RoundSolver { NodeBudget = _nodeBudget, TimeBudgetMs = _timeMs };
            return solver.Evaluate(state);
        }

        private static void PressInsightOk(GameController gc, InsightDialog dialog)
        {
            Button button = gc.UI.GetButton(dialog.InsightOnOpponent ? "ok_insight_opponent" : "ok_insight");
            if (button != null)
            {
                button.onClick.Invoke();
            }
        }

        private static void PressDemandOk(GameController gc)
        {
            Button button = gc.UI.GetButton("ok_demand");
            if (button != null)
            {
                button.onClick.Invoke();
            }
        }

        private static string Values(Card3D card)
        {
            return card == null ? "?" : string.Join("/", card.GameCard.Values.Select(v => v.ToString()));
        }

        private static string Format(float value)
        {
            return value.ToString("+0.##;-0.##;0");
        }
    }
}
