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
    ///
    /// All candidate sets are evaluated as one native batch on the solver worker, so the
    /// main thread only captures the position, then waits for the completed values.
    /// </summary>
    internal sealed class AutoSelector
    {
        private const float SettleSeconds = 1.25f;
        private const int MaxInsightCandidates = 160;
        private const int ShuffleSamples = 4;

        private sealed class InsightCandidate
        {
            public List<SolverCard> Perm;
            public int Split;
        }

        private sealed class ChoiceItem
        {
            public Card3D Card;
            public SolverCard Sim;
        }

        private float _openedAt = -1f;
        private bool _handled;
        private string _status = "";
        private int _nodeBudget = 8000;
        private int _timeMs = 10;

        /// <summary>One in-flight native batch for the active dialog; dropped when it closes.</summary>
        private SolverJob<float[]> _dialogJob;

        // insight context
        private List<InsightCandidate> _insightCandidates;
        private bool _insightOnOpponent;
        private int _insightShown;
        private GameObject[] _insightObjects;
        private Dictionary<SolverCard, GameObject> _insightGoByCard;
        private GameObject _insightPlaceholder;

        // demand context
        private List<Card3D> _demandCards;

        // card choice context
        private List<ChoiceItem> _choiceItems;
        private Card3D _choiceCard;
        private float _choiceStartedAt = -1f;

        // shuffle context
        private SolverJob<float[]> _shuffleJob;
        private bool _shuffleDecided;
        private bool _shuffleOwnDeck;

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
                ClearSubmitted();
            }
        }

        /// <summary>Forget a dialog that closed; its worker job finishes into a dropped slot.</summary>
        private void ResetDialog()
        {
            _openedAt = -1f;
            _handled = false;
            _choiceCard = null;
            _choiceStartedAt = -1f;
            ClearSubmitted();
        }

        private void ClearSubmitted()
        {
            _dialogJob = null;
            _insightCandidates = null;
            _insightObjects = null;
            _insightGoByCard = null;
            _insightPlaceholder = null;
            _demandCards = null;
            _choiceItems = null;
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
                if (_dialogJob == null)
                {
                    bool submitted;
                    try
                    {
                        submitted = StartInsight(gc, dialog, line);
                    }
                    catch (Exception e)
                    {
                        OptimalPlayPlugin.Log.LogWarning($"Insight automation failed: {e.Message}");
                        submitted = false;
                    }
                    if (!submitted)
                    {
                        _handled = true;
                    }
                }
                if (_dialogJob != null)
                {
                    if (!_dialogJob.Done)
                    {
                        _status = "insight: thinking";
                        return;
                    }
                    SolverJob<float[]> job = _dialogJob;
                    _dialogJob = null;
                    try
                    {
                        ApplyBestInsight(line, job);
                    }
                    catch (Exception e)
                    {
                        OptimalPlayPlugin.Log.LogWarning($"Insight automation failed: {e.Message}");
                    }
                    _handled = true;
                }
            }

            PressInsightOk(gc, dialog);
        }

        /// <summary>Builds the candidate set and submits it; false when there is nothing to do.</summary>
        private bool StartInsight(GameController gc, InsightDialog dialog, ReorderCardLine line)
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
                return false;
            }

            bool onOpponent = dialog.InsightOnOpponent;
            SolverState baseState = AutoPilot.Capture(gc);
            SolverSide side = onOpponent ? baseState.O : baseState.P;
            if (side.Deck.Count < shownCount)
            {
                return false;
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
            var plan = new List<Action<SolverBuffer>>();
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
                    int[] indices = NativeSolver.IndexOrder(order, side.Deck);
                    if (indices == null)
                    {
                        continue;
                    }
                    int sideIndex = onOpponent ? SolverProtocol.SideOpponent : SolverProtocol.SidePlayer;
                    plan.Add(buffer => SolverProtocol.WriteSetDeckOrder(buffer, sideIndex, indices));
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
                return false;
            }

            byte[] batch = NativeSolver.WriteBatch(baseState, plan);
            int nodes = _nodeBudget;
            int timeMs = _timeMs;
            _insightCandidates = candidates;
            _insightOnOpponent = onOpponent;
            _insightShown = shownCount;
            _insightObjects = objects;
            _insightGoByCard = goByCard;
            _insightPlaceholder = placeholder;
            _dialogJob = SolverWorker.Post(() => NativeSolver.EvaluateBatch(batch, plan.Count, nodes, timeMs));
            return true;
        }

        private void ApplyBestInsight(ReorderCardLine line, SolverJob<float[]> job)
        {
            List<InsightCandidate> candidates = _insightCandidates;
            bool onOpponent = _insightOnOpponent;
            int shownCount = _insightShown;
            GameObject[] objects = _insightObjects;
            Dictionary<SolverCard, GameObject> goByCard = _insightGoByCard;
            GameObject placeholder = _insightPlaceholder;
            ClearSubmitted();

            if (job.Error != null)
            {
                throw job.Error;
            }
            if (candidates == null || objects == null)
            {
                return;
            }

            float[] values = job.Result;
            int evaluated = Mathf.Min(values.Length, candidates.Count);
            int bestIndex = -1;
            float bestValue = float.NegativeInfinity;
            for (int i = 0; i < evaluated; i++)
            {
                if (values[i] > bestValue)
                {
                    bestValue = values[i];
                    bestIndex = i;
                }
            }
            if (bestIndex < 0)
            {
                return;
            }
            InsightCandidate best = candidates[bestIndex];

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
                + $"value {SolverReport.Format(bestValue)} over {evaluated}/{candidates.Count} candidates.");
            _status = $"insight: reorder (ev {SolverReport.Format(bestValue)})";
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
                if (_dialogJob == null)
                {
                    bool submitted;
                    try
                    {
                        submitted = StartDemand(gc, line);
                    }
                    catch (Exception e)
                    {
                        OptimalPlayPlugin.Log.LogWarning($"Demand automation failed: {e.Message}");
                        submitted = false;
                    }
                    if (!submitted)
                    {
                        _handled = true;
                    }
                }
                if (_dialogJob != null)
                {
                    if (!_dialogJob.Done)
                    {
                        _status = "demand: thinking";
                        return;
                    }
                    SolverJob<float[]> job = _dialogJob;
                    _dialogJob = null;
                    try
                    {
                        ApplyBestDemand(line, job);
                    }
                    catch (Exception e)
                    {
                        OptimalPlayPlugin.Log.LogWarning($"Demand automation failed: {e.Message}");
                    }
                    _handled = true;
                }
            }

            // A click selects and advances; only a skipped demand needs the OK button.
            if (line.SelectedCard == null)
            {
                PressDemandOk(gc);
            }
        }

        private bool StartDemand(GameController gc, SelectCardLine line)
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
                return false;
            }

            PlayerState player = gc.State.Player;
            Card3D[] pile = player.DrawPile.Cards;
            SolverState baseState = AutoPilot.Capture(gc);

            var plan = new List<Action<SolverBuffer>>();
            var cards = new List<Card3D>();
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
                int capturedIndex = deckIndex;
                plan.Add(buffer =>
                    SolverProtocol.WriteTakeDeckToSleeve(buffer, SolverProtocol.SidePlayer, capturedIndex));
                cards.Add(card);
            }
            if (cards.Count == 0)
            {
                return false;
            }

            // Skipping is a candidate too, so the comparison uses the same search.
            plan.Add(buffer => { });

            byte[] batch = NativeSolver.WriteBatch(baseState, plan);
            int nodes = _nodeBudget;
            int timeMs = _timeMs;
            _demandCards = cards;
            _dialogJob = SolverWorker.Post(() => NativeSolver.EvaluateBatch(batch, plan.Count, nodes, timeMs));
            return true;
        }

        private void ApplyBestDemand(SelectCardLine line, SolverJob<float[]> job)
        {
            List<Card3D> cards = _demandCards;
            _demandCards = null;

            if (job.Error != null)
            {
                throw job.Error;
            }
            if (cards == null)
            {
                return;
            }

            float[] values = job.Result;
            if (values.Length < cards.Count + 1)
            {
                return;
            }
            float skipValue = values[cards.Count];

            var ranked = new List<(Card3D Card, float Value)>(cards.Count);
            for (int i = 0; i < cards.Count; i++)
            {
                ranked.Add((cards[i], values[i]));
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
                        $"Demand: taking [{Values(card)}] (value {SolverReport.Format(value)} vs skip {SolverReport.Format(skipValue)}).");
                    _status = $"demand: take [{Values(card)}] (ev {SolverReport.Format(value)})";
                    return;
                }
            }

            OptimalPlayPlugin.Log.LogInfo(
                $"Demand: skipping (best {SolverReport.Format(ranked[0].Value)} vs skip {SolverReport.Format(skipValue)}).");
            _status = "demand: skip";
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
                ClearSubmitted();
                _choiceCard = null;
                _handled = false;
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

            if (!_handled)
            {
                if (_dialogJob == null)
                {
                    bool submitted;
                    try
                    {
                        submitted = StartCardChoice(gc, choices);
                    }
                    catch (Exception e)
                    {
                        OptimalPlayPlugin.Log.LogWarning($"Card choice failed ({e.Message}); taking the first candidate.");
                        submitted = false;
                    }
                    if (!submitted)
                    {
                        _choiceCard = choices[0];
                        _status = "card choice: fallback";
                        _handled = true;
                    }
                }
                if (_dialogJob != null)
                {
                    if (!_dialogJob.Done)
                    {
                        _status = "card choice: thinking";
                        return;
                    }
                    SolverJob<float[]> job = _dialogJob;
                    _dialogJob = null;
                    try
                    {
                        _choiceCard = ApplyBestCardChoice(job);
                    }
                    catch (Exception e)
                    {
                        OptimalPlayPlugin.Log.LogWarning($"Card choice failed ({e.Message}); taking the first candidate.");
                        _choiceCard = null;
                    }
                    if (_choiceCard == null)
                    {
                        _choiceCard = choices[0];
                        _status = "card choice: fallback";
                    }
                    _handled = true;
                }
            }

            if (_choiceCard != null)
            {
                ActiveCardChoice.SetSelectedCard(_choiceCard);
            }
        }

        private bool StartCardChoice(GameController gc, Card3D[] choices)
        {
            SolverState baseState = AutoPilot.Capture(gc);
            Card3D[] table = gc.State.Player.TableDropArea.Cards;
            var plan = new List<Action<SolverBuffer>>();
            var items = new List<ChoiceItem>();
            foreach (Card3D card in choices)
            {
                if (card == null)
                {
                    continue;
                }
                int index = Array.IndexOf(table, card);
                if (index < 0 || index >= baseState.P.Table.Count)
                {
                    continue;
                }
                int capturedIndex = index;
                plan.Add(buffer =>
                    SolverProtocol.WriteMoveTableToSleeve(buffer, SolverProtocol.SidePlayer, capturedIndex));
                items.Add(new ChoiceItem { Card = card, Sim = SolverCard.From(card) });
            }
            if (items.Count == 0)
            {
                return false;
            }

            byte[] batch = NativeSolver.WriteBatch(baseState, plan);
            Settings cfg = OptimalPlayPlugin.Cfg;
            int nodes = Math.Max(1000, cfg.SearchNodeBudget.Value);
            int timeMs = Math.Max(20, cfg.SearchTimeMs.Value);
            _choiceItems = items;
            _dialogJob = SolverWorker.Post(() => NativeSolver.EvaluateBatch(batch, items.Count, nodes, timeMs));
            return true;
        }

        private Card3D ApplyBestCardChoice(SolverJob<float[]> job)
        {
            List<ChoiceItem> items = _choiceItems;
            _choiceItems = null;

            if (job.Error != null)
            {
                throw job.Error;
            }
            if (items == null)
            {
                return null;
            }

            float[] values = job.Result;
            var ranked = new List<(Card3D Card, SolverCard Sim, float Value)>(items.Count);
            for (int i = 0; i < items.Count && i < values.Length; i++)
            {
                ranked.Add((items[i].Card, items[i].Sim, values[i]));
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
                string options = string.Join(", ",
                    ranked.Select(r => $"{r.Sim.Label}={SolverReport.Format(r.Value)}"));
                OptimalPlayPlugin.Log.LogInfo(
                    $"Card choice ({source}): sleeving {ranked[0].Sim.Label} (value {SolverReport.Format(ranked[0].Value)}; "
                    + $"options: {options}).");
            }
            _status = $"card choice: sleeve {ranked[0].Sim.Label} (ev {SolverReport.Format(ranked[0].Value)})";
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
                _shuffleJob = null;
                return;
            }
            if (!cfg.AutoSelectShuffle.Value)
            {
                _status = "shuffle: manual";
                return;
            }

            if (!_shuffleDecided)
            {
                if (_shuffleJob == null)
                {
                    bool submitted;
                    try
                    {
                        submitted = StartShuffle(gc);
                    }
                    catch (Exception e)
                    {
                        OptimalPlayPlugin.Log.LogWarning($"Shuffle choice failed ({e.Message}); shuffling the opponent deck.");
                        submitted = false;
                    }
                    if (!submitted)
                    {
                        _shuffleOwnDeck = false;
                        _shuffleDecided = true;
                    }
                }
                if (_shuffleJob != null)
                {
                    if (!_shuffleJob.Done)
                    {
                        _status = "shuffle: thinking";
                        return;
                    }
                    SolverJob<float[]> job = _shuffleJob;
                    _shuffleJob = null;
                    try
                    {
                        _shuffleOwnDeck = ApplyShuffle(job);
                    }
                    catch (Exception e)
                    {
                        _shuffleOwnDeck = false;
                        OptimalPlayPlugin.Log.LogWarning($"Shuffle choice failed ({e.Message}); shuffling the opponent deck.");
                    }
                    _shuffleDecided = true;
                }
            }

            Button chosen = _shuffleOwnDeck ? playerButton : opponentButton;
            chosen.onClick.Invoke();
            _status = _shuffleOwnDeck ? "shuffle: own deck" : "shuffle: opponent deck";
        }

        private bool StartShuffle(GameController gc)
        {
            SolverState sim = AutoPilot.Capture(gc);
            if (sim.P.Deck.Count == 0 || sim.O.Deck.Count == 0)
            {
                return false;
            }

            var rng = new System.Random(Environment.TickCount);
            var plan = new List<Action<SolverBuffer>>();
            for (int i = 0; i < ShuffleSamples; i++)
            {
                int[] ownOrder = NativeSolver.IndexOrder(Shuffled(sim.P.Deck, rng), sim.P.Deck);
                if (ownOrder == null)
                {
                    return false;
                }
                plan.Add(buffer => SolverProtocol.WriteSetDeckOrder(buffer, SolverProtocol.SidePlayer, ownOrder));

                int[] opponentOrder = NativeSolver.IndexOrder(Shuffled(sim.O.Deck, rng), sim.O.Deck);
                if (opponentOrder == null)
                {
                    return false;
                }
                plan.Add(buffer =>
                    SolverProtocol.WriteSetDeckOrder(buffer, SolverProtocol.SideOpponent, opponentOrder));
            }

            byte[] batch = NativeSolver.WriteBatch(sim, plan);
            int nodes = _nodeBudget;
            int timeMs = _timeMs;
            _shuffleJob = SolverWorker.Post(() => NativeSolver.EvaluateBatch(batch, plan.Count, nodes, timeMs));
            return true;
        }

        private static bool ApplyShuffle(SolverJob<float[]> job)
        {
            if (job.Error != null)
            {
                throw job.Error;
            }

            float[] values = job.Result;
            if (values.Length < 2 * ShuffleSamples)
            {
                return false;
            }
            float ownSum = 0f;
            float opponentSum = 0f;
            for (int i = 0; i < 2 * ShuffleSamples; i += 2)
            {
                ownSum += values[i];
                opponentSum += values[i + 1];
            }

            float ownValue = ownSum / ShuffleSamples;
            float opponentValue = opponentSum / ShuffleSamples;
            OptimalPlayPlugin.Log.LogInfo(
                $"Shuffle choice: own deck {SolverReport.Format(ownValue)} vs opponent deck {SolverReport.Format(opponentValue)} (sampled).");
            return ownValue > opponentValue;
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
    }
}
