using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BlackJacket.OptimalPlay
{
    internal enum MoveKind
    {
        Pass,
        PlayTop,
        SleeveTop,
        PlaySleeve,
    }

    internal struct SolverMove
    {
        public MoveKind Kind;
        public int SleeveIndex;
        public bool ToOpponent;

        public string Label
        {
            get
            {
                switch (Kind)
                {
                    case MoveKind.PlayTop: return ToOpponent ? "play top card to opponent" : "play top card";
                    case MoveKind.SleeveTop: return "sleeve top card";
                    case MoveKind.PlaySleeve: return ToOpponent ? "play sleeve card to opponent" : "play sleeve card";
                    default: return "pass";
                }
            }
        }
    }

    internal sealed class MoveEvaluation
    {
        public SolverMove Move;
        public float Value;
    }

    /// <summary>
    /// A card reduced to the information that matters for the value game.
    /// </summary>
    internal sealed class SolverCard
    {
        public int[] Values;
        public bool IsAce;
        public int Highest;
        public bool IsHollow;
        public bool AlwaysInsight;
        public bool CanPlayOpponent;
        public bool Broken;
        public string Name;
        public string Effect;
        public string Sig;

        /// <summary>A card whose best possible contribution is zero or less (e.g. an awakened Heart).</summary>
        public bool IsDead
        {
            get
            {
                int best = int.MinValue;
                foreach (int v in Values)
                {
                    if (v > best)
                    {
                        best = v;
                    }
                }
                return best <= 0;
            }
        }

        /// <summary>Short human readable label, e.g. "Hearts_7_Upgraded[-7]".</summary>
        public string Label => $"{Name}[{string.Join("/", Values)}]";

        public static SolverCard From(Card3D card)
        {
            GameCard gc = card.GameCard;
            int[] values = gc.Values;
            string name = gc.name ?? "card";
            if (name.StartsWith("GameCard_"))
            {
                name = name.Substring("GameCard_".Length);
            }
            return new SolverCard
            {
                Values = values,
                IsAce = gc.Id == "ace",
                Highest = gc.HighestValue,
                IsHollow = gc.IsHollow,
                AlwaysInsight = gc.OpponentAlwaysHasInsightOnThisCard,
                CanPlayOpponent = gc.CanBePlayedInOpponentsSlots,
                Broken = values.Any(v => v < 0),
                Name = name,
                Effect = string.IsNullOrEmpty(gc.EffectText) ? null : gc.EffectText.Replace("\n", " "),
                Sig = (gc.Id == "ace" ? "a" : "n")
                    + (gc.IsHollow ? "h" : "")
                    + (gc.OpponentAlwaysHasInsightOnThisCard ? "i" : "")
                    + (gc.CanBePlayedInOpponentsSlots ? "o" : "")
                    + (values.Any(v => v < 0) ? "b" : "")
                    + ":" + string.Join("|", values),
            };
        }
    }

    internal sealed class SolverSide
    {
        // Deck and Discard are shared between clones; only DeckPos moves forward.
        public List<SolverCard> Deck;
        public List<SolverCard> Discard;
        public int DeckPos;

        public List<SolverCard> Sleeve = new List<SolverCard>();
        public List<SolverCard> Table = new List<SolverCard>();

        public bool Passed;
        public bool OutOfCards;
        public int Capacity;
        public int Bet;
        public int SleeveDraws;

        public SolverCard Top => DeckPos < Deck.Count ? Deck[DeckPos] : null;

        public SolverSide Clone()
        {
            return new SolverSide
            {
                Deck = Deck,
                Discard = Discard,
                DeckPos = DeckPos,
                Sleeve = new List<SolverCard>(Sleeve),
                Table = new List<SolverCard>(Table),
                Passed = Passed,
                OutOfCards = OutOfCards,
                Capacity = Capacity,
                Bet = Bet,
                SleeveDraws = SleeveDraws,
            };
        }
    }

    internal sealed class SolverState
    {
        public int PlainTarget;
        public int PMods;
        public int OMods;
        public int HoldsAt;
        public int InsightLeft;
        public int SleeveSize;
        public int[] SleeveCosts;
        public int Payable;
        public bool Uprising;
        public bool Supper;

        public SolverSide P = new SolverSide();
        public SolverSide O = new SolverSide();

        // Table/capacity are the only inputs of these values that change during a search,
        // and recomputing them (which enumerates every value combination) is the hot path.
        private int? _pValue;
        private int? _oValue;

        public int PTarget => PlainTarget + PMods - (Uprising ? P.Capacity * 2 : 0);
        public int OTarget => PlainTarget + OMods - (Uprising ? O.Capacity * 2 : 0);

        public int PValue
        {
            get
            {
                if (!_pValue.HasValue)
                {
                    _pValue = RoundSolver.Total(P.Table, PTarget) + PMods;
                }
                return _pValue.Value;
            }
        }

        public int OValue
        {
            get
            {
                if (!_oValue.HasValue)
                {
                    _oValue = RoundSolver.Total(O.Table, OTarget) + OMods;
                }
                return _oValue.Value;
            }
        }

        public bool PBusted => PValue > PlainTarget;
        public bool OBusted => OValue > PlainTarget;
        public bool PBJ => RoundSolver.HasBlackJack(P.Table);
        public bool OBJ => RoundSolver.HasBlackJack(O.Table);

        public void InvalidateValues()
        {
            _pValue = null;
            _oValue = null;
        }

        public SolverState Clone()
        {
            return new SolverState
            {
                PlainTarget = PlainTarget,
                PMods = PMods,
                OMods = OMods,
                HoldsAt = HoldsAt,
                InsightLeft = InsightLeft,
                SleeveSize = SleeveSize,
                SleeveCosts = SleeveCosts,
                Payable = Payable,
                Uprising = Uprising,
                Supper = Supper,
                _pValue = _pValue,
                _oValue = _oValue,
                P = P.Clone(),
                O = O.Clone(),
            };
        }
    }

    /// <summary>
    /// Perfect-information solver for one round of the draw phase.
    ///
    /// Both draw piles, the sleeve, both tables and the opponent's deterministic policy are
    /// known, so the whole round is a DAG: every action either advances a deck position or
    /// moves a card. The solver does a full backward induction over the player's choices and
    /// returns the move with the best coin outcome (win = opponent's bet, loss = own bet,
    /// tie = 0; sleeve costs are added to the own bet, exactly like the game does).
    /// </summary>
    internal sealed class RoundSolver
    {
        public int NodeBudget = 250000;
        public int TimeBudgetMs = 250;

        private sealed class MoveState
        {
            public SolverMove Move;
            public SolverState State;
        }

        private Dictionary<string, float> _memo;
        private int _nodes;
        private bool _aborted;
        private int _startMs;
        private readonly StringBuilder _sb = new StringBuilder(256);
        private readonly List<SolverCard> _sortScratch = new List<SolverCard>();

        /// <summary>Set when the last decision needed a heuristic nudge (for logging).</summary>
        public string Note { get; private set; }

        public SolverMove BestMove(SolverState root, out float value, out int nodes, out bool aborted,
            out List<MoveEvaluation> evaluations)
        {
            _memo = new Dictionary<string, float>();
            _nodes = 0;
            _aborted = false;
            _startMs = Environment.TickCount;
            evaluations = new List<MoveEvaluation>();
            Note = null;

            SolverMove best = new SolverMove { Kind = MoveKind.Pass };
            float bestValue = float.NegativeInfinity;

            foreach (MoveState entry in EnumerateMoves(root))
            {
                float v = entry.Move.Kind == MoveKind.SleeveTop
                    ? BestPlayer(entry.State)
                    : AfterPlayerAction(entry.State);
                evaluations.Add(new MoveEvaluation { Move = entry.Move, Value = v });
                if (v > bestValue)
                {
                    bestValue = v;
                    best = entry.Move;
                }
                if (_aborted)
                {
                    break;
                }
            }

            value = bestValue;
            nodes = _nodes;
            aborted = _aborted;

            if (_aborted || evaluations.Count == 0)
            {
                best = GreedyMove(root);
                value = float.NaN;
                return best;
            }

            // Deck-progress tie-break: losing a round costs the bet no matter how it is lost,
            // so when the round is lost (or drawn) and passing ties with playing, cycle a dead
            // card instead of freezing the deck on it. Passing forever would otherwise leave
            // a stuck negative card (e.g. an awakened Heart) on top of the draw pile.
            if (best.Kind == MoveKind.Pass && bestValue <= 0.0001f)
            {
                SolverMove progress = ProgressMove(root, evaluations, bestValue);
                if (progress.Kind != MoveKind.Pass)
                {
                    best = progress;
                    Note = "progress tie-break: played a dead card instead of passing";
                }
            }
            return best;
        }

        private static SolverMove ProgressMove(SolverState root, List<MoveEvaluation> evaluations, float best)
        {
            const float eps = 0.0001f;
            SolverMove sleeve = default;
            bool hasSleeve = false;
            foreach (MoveEvaluation e in evaluations)
            {
                if (e.Value < best - eps)
                {
                    continue;
                }
                switch (e.Move.Kind)
                {
                    case MoveKind.PlayTop:
                        if (root.P.Top != null && root.P.Top.IsDead)
                        {
                            return e.Move;
                        }
                        break;
                    case MoveKind.PlaySleeve:
                        if (e.Move.SleeveIndex >= 0 && e.Move.SleeveIndex < root.P.Sleeve.Count
                            && root.P.Sleeve[e.Move.SleeveIndex].IsDead && !hasSleeve)
                        {
                            sleeve = e.Move;
                            hasSleeve = true;
                        }
                        break;
                }
            }
            return hasSleeve ? sleeve : new SolverMove { Kind = MoveKind.Pass };
        }

        /// <summary>
        /// Value of a position, running the same game loop the draw phase would, without
        /// choosing a move at the root. Used to score candidate deck orders and card picks.
        /// </summary>
        public float Evaluate(SolverState root)
        {
            _memo = new Dictionary<string, float>();
            _nodes = 0;
            _aborted = false;
            _startMs = Environment.TickCount;
            return AfterPlayerAction(root);
        }

        // ---------------------------------------------------------------- search

        /// <summary>Best value for the player when it is the player's turn to choose.</summary>
        private float BestPlayer(SolverState s)
        {
            string key = Key('P', s);
            if (_memo.TryGetValue(key, out float cached))
            {
                return cached;
            }
            if (BudgetExceeded())
            {
                return Resolve(s);
            }

            float best = float.NegativeInfinity;
            foreach (MoveState entry in EnumerateMoves(s))
            {
                float v = entry.Move.Kind == MoveKind.SleeveTop
                    ? BestPlayer(entry.State)
                    : AfterPlayerAction(entry.State);
                if (v > best)
                {
                    best = v;
                }
                if (_aborted)
                {
                    return Resolve(s);
                }
            }

            if (best == float.NegativeInfinity)
            {
                best = Resolve(s);
            }
            _memo[key] = best;
            return best;
        }

        /// <summary>
        /// Continuation right after the player's turn ended (card played or passed):
        /// the opponent unpass check runs, then the game loop resumes.
        /// </summary>
        private float AfterPlayerAction(SolverState s)
        {
            string key = Key('A', s);
            if (_memo.TryGetValue(key, out float cached))
            {
                return cached;
            }
            if (BudgetExceeded())
            {
                return Resolve(s);
            }

            OpponentUnpassCheck(s);
            if (s.P.Passed && s.O.Passed)
            {
                // Reveal face-down cards and check once more.
                OpponentUnpassCheck(s);
                if (s.O.Passed)
                {
                    float resolved = Resolve(s);
                    _memo[key] = resolved;
                    return resolved;
                }
            }

            float result = Run(s);
            _memo[key] = result;
            return result;
        }

        /// <summary>Game loop: opponent turns and resolutions until the player has to choose again.</summary>
        private float Run(SolverState s)
        {
            string key = Key('R', s);
            if (_memo.TryGetValue(key, out float cached))
            {
                return cached;
            }
            if (BudgetExceeded())
            {
                return Resolve(s);
            }

            float result;
            while (true)
            {
                if (s.P.Passed && s.O.Passed)
                {
                    result = Resolve(s);
                    break;
                }

                if (!s.O.Passed)
                {
                    OpponentAct(s);
                    OpponentUnpassCheck(s);
                }

                if (!s.P.Passed)
                {
                    result = BestPlayer(s);
                    break;
                }

                // Player already passed: finish the loop body like the game does.
                OpponentUnpassCheck(s);
                if (s.P.Passed && s.O.Passed)
                {
                    OpponentUnpassCheck(s);
                    if (s.O.Passed)
                    {
                        result = Resolve(s);
                        break;
                    }
                }
            }

            _memo[key] = result;
            return result;
        }

        // ---------------------------------------------------------------- rules

        private List<MoveState> EnumerateMoves(SolverState s)
        {
            var moves = new List<MoveState>();

            if (CanPass(s))
            {
                SolverState n = s.Clone();
                n.P.Passed = true;
                moves.Add(new MoveState { Move = new SolverMove { Kind = MoveKind.Pass }, State = n });
            }

            SolverCard top = s.P.Top;
            if (top != null && s.P.Capacity > 0)
            {
                SolverState n = s.Clone();
                n.P.DeckPos++;
                n.P.Table.Add(top);
                n.P.Capacity--;
                if (top.IsHollow)
                {
                    n.P.Capacity++;
                }
                n.InvalidateValues();
                moves.Add(new MoveState { Move = new SolverMove { Kind = MoveKind.PlayTop }, State = n });
            }
            if (top != null && top.CanPlayOpponent && s.O.Capacity > 0)
            {
                SolverState n = s.Clone();
                n.P.DeckPos++;
                n.O.Table.Add(top);
                n.O.Capacity--;
                if (top.IsHollow)
                {
                    n.O.Capacity++;
                }
                n.InvalidateValues();
                moves.Add(new MoveState
                {
                    Move = new SolverMove { Kind = MoveKind.PlayTop, ToOpponent = true },
                    State = n,
                });
            }

            if (s.P.Capacity > 0 || s.O.Capacity > 0)
            {
                for (int i = 0; i < s.P.Sleeve.Count; i++)
                {
                    SolverCard card = s.P.Sleeve[i];
                    if (s.P.Capacity > 0)
                    {
                        SolverState n = s.Clone();
                        n.P.Sleeve.RemoveAt(i);
                        n.P.Table.Add(card);
                        n.P.Capacity--;
                        if (card.IsHollow)
                        {
                            n.P.Capacity++;
                        }
                        n.InvalidateValues();
                        moves.Add(new MoveState
                        {
                            Move = new SolverMove { Kind = MoveKind.PlaySleeve, SleeveIndex = i },
                            State = n,
                        });
                    }
                    if (card.CanPlayOpponent && s.O.Capacity > 0)
                    {
                        SolverState n = s.Clone();
                        n.P.Sleeve.RemoveAt(i);
                        n.O.Table.Add(card);
                        n.O.Capacity--;
                        if (card.IsHollow)
                        {
                            n.O.Capacity++;
                        }
                        n.InvalidateValues();
                        moves.Add(new MoveState
                        {
                            Move = new SolverMove { Kind = MoveKind.PlaySleeve, SleeveIndex = i, ToOpponent = true },
                            State = n,
                        });
                    }
                }
            }

            if (CanSleeve(s))
            {
                int cost = SleeveCost(s);
                SolverState n = s.Clone();
                n.P.DeckPos++;
                int max = Math.Max(s.SleeveSize, 1);
                if (n.P.Sleeve.Count >= max)
                {
                    n.P.Sleeve.RemoveAt(0);
                }
                n.P.Sleeve.Add(top);
                n.P.SleeveDraws++;
                n.P.Bet += cost;
                n.Payable -= cost;
                moves.Add(new MoveState { Move = new SolverMove { Kind = MoveKind.SleeveTop }, State = n });
            }

            return moves;
        }

        private bool CanPass(SolverState s)
        {
            if (s.Supper)
            {
                // Supper.PlayerCanPass: only allowed when the table has no free slot.
                return s.P.Capacity <= 0;
            }
            return true;
        }

        private bool CanSleeve(SolverState s)
        {
            return s.SleeveSize > 0
                && s.P.Top != null
                && s.SleeveCosts != null
                && s.SleeveCosts.Length > 0
                && s.Payable >= SleeveCost(s);
        }

        private int SleeveCost(SolverState s)
        {
            int[] costs = s.SleeveCosts;
            if (costs == null || costs.Length == 0)
            {
                return int.MaxValue;
            }
            return costs[Math.Min(costs.Length - 1, s.P.SleeveDraws)];
        }

        // ---------------------------------------------------------------- opponent policy

        private void OpponentAct(SolverState s)
        {
            SolverSide o = s.O;
            if (o.Top == null)
            {
                // The game would reshuffle the discard pile; the new order is random, so the
                // deterministic search stops here and treats the opponent as standing.
                o.OutOfCards = true;
                o.Passed = true;
                return;
            }

            if (!OppWillDraw(s))
            {
                o.Passed = true;
                return;
            }

            SolverCard card = o.Top;
            o.DeckPos++;
            o.Table.Add(card);
            o.Capacity--;
            if (card.IsHollow)
            {
                o.Capacity++;
            }
            s.InvalidateValues();
            s.InsightLeft = Math.Max(0, s.InsightLeft - 1);
        }

        private bool OppWillDraw(SolverState s)
        {
            SolverSide o = s.O;
            if (o.OutOfCards)
            {
                return false;
            }
            if (o.DeckPos >= o.Deck.Count && o.Discard.Count == 0)
            {
                return false;
            }
            if (s.OBJ || s.OValue == s.OTarget)
            {
                return false;
            }
            if (s.PValue > s.PTarget)
            {
                return false;
            }
            if (s.P.Passed && s.PValue < s.OValue)
            {
                return false;
            }

            SolverCard peek = o.Top;
            bool canPlace = o.Capacity > 0;
            if (!canPlace)
            {
                return false;
            }

            if (peek != null && (s.InsightLeft > 0 || peek.AlwaysInsight))
            {
                var withPeek = new List<SolverCard>(o.Table) { peek };
                int raw = Total(withPeek, s.OTarget);
                if (raw <= s.OTarget && raw + s.OMods >= s.OValue)
                {
                    return true;
                }
            }

            return s.OValue < s.HoldsAt;
        }

        private void OpponentUnpassCheck(SolverState s)
        {
            if (s.O.Passed && OppWillDraw(s))
            {
                s.O.Passed = false;
            }
        }

        // ---------------------------------------------------------------- resolution

        private float Resolve(SolverState s)
        {
            int winner = 0;
            bool pBJ = s.PBJ;
            bool oBJ = s.OBJ;

            if (pBJ && !oBJ)
            {
                winner = 1;
            }
            else if (oBJ && !pBJ)
            {
                winner = -1;
            }
            else if (pBJ && oBJ)
            {
                winner = 0;
            }
            else
            {
                int pv = s.PValue;
                int ov = s.OValue;
                if (s.PBusted && s.OBusted)
                {
                    winner = 0;
                }
                else if (pv > s.PlainTarget)
                {
                    winner = -1;
                }
                else if (ov > s.PlainTarget)
                {
                    winner = 1;
                }
                else if (pv > ov)
                {
                    winner = 1;
                }
                else if (ov > pv)
                {
                    winner = -1;
                }
                else
                {
                    int ph = Highest(s.P.Table);
                    int oh = Highest(s.O.Table);
                    if (ph > oh)
                    {
                        winner = 1;
                    }
                    else if (oh > ph)
                    {
                        winner = -1;
                    }
                }
            }

            if (winner > 0)
            {
                return s.O.Bet;
            }
            if (winner < 0)
            {
                return -s.P.Bet;
            }
            return 0f;
        }

        // ---------------------------------------------------------------- helpers

        // Total() is called in a tight loop; the buffers are reused to avoid one HashSet
        // allocation per card per node. Nothing calls Total() reentrantly.
        [ThreadStatic] private static HashSet<int> _sumsBufferA;
        [ThreadStatic] private static HashSet<int> _sumsBufferB;

        internal static int Total(List<SolverCard> cards, int target)
        {
            HashSet<int> sums = _sumsBufferA ?? (_sumsBufferA = new HashSet<int>());
            HashSet<int> next = _sumsBufferB ?? (_sumsBufferB = new HashSet<int>());
            sums.Clear();
            sums.Add(0);
            foreach (SolverCard card in cards)
            {
                next.Clear();
                foreach (int sum in sums)
                {
                    foreach (int value in card.Values)
                    {
                        next.Add(sum + value);
                    }
                }
                HashSet<int> swap = sums;
                sums = next;
                next = swap;
            }

            int bestBelow = int.MinValue;
            int minAbove = int.MaxValue;
            foreach (int sum in sums)
            {
                if (sum <= target)
                {
                    if (sum > bestBelow)
                    {
                        bestBelow = sum;
                    }
                }
                else if (sum < minAbove)
                {
                    minAbove = sum;
                }
            }
            return bestBelow != int.MinValue ? bestBelow : minAbove;
        }

        internal static bool HasBlackJack(List<SolverCard> cards)
        {
            if (cards.Count != 2)
            {
                return false;
            }
            SolverCard a = cards[0];
            SolverCard b = cards[1];
            return (a.IsAce && HasValue(b, 10)) || (b.IsAce && HasValue(a, 10));
        }

        private static bool HasValue(SolverCard card, int value)
        {
            foreach (int v in card.Values)
            {
                if (v == value)
                {
                    return true;
                }
            }
            return false;
        }

        private static int Highest(List<SolverCard> cards)
        {
            if (cards.Count == 0)
            {
                return int.MinValue;
            }
            int best = int.MinValue;
            foreach (SolverCard card in cards)
            {
                if (card.Highest > best)
                {
                    best = card.Highest;
                }
            }
            return best;
        }

        private bool BudgetExceeded()
        {
            if (_aborted)
            {
                return true;
            }
            _nodes++;
            if (_nodes > NodeBudget)
            {
                _aborted = true;
                return true;
            }
            if ((_nodes & 1023) == 0 && Environment.TickCount - _startMs > TimeBudgetMs)
            {
                _aborted = true;
                return true;
            }
            return false;
        }

        private SolverMove GreedyMove(SolverState s)
        {
            if (s.OBusted && !s.PBusted)
            {
                return new SolverMove { Kind = MoveKind.Pass };
            }
            if (s.O.Passed && s.PValue > s.OValue)
            {
                return new SolverMove { Kind = MoveKind.Pass };
            }

            SolverCard top = s.P.Top;
            if (top != null && s.P.Capacity > 0)
            {
                var withTop = new List<SolverCard>(s.P.Table) { top };
                int value = Total(withTop, s.PTarget) + s.PMods;
                if (value <= s.PlainTarget && (value > s.PValue || s.OValue > s.PValue))
                {
                    return new SolverMove { Kind = MoveKind.PlayTop };
                }
            }

            if (CanSleeve(s))
            {
                return new SolverMove { Kind = MoveKind.SleeveTop };
            }

            if (s.P.Capacity > 0)
            {
                int bestIndex = -1;
                int bestValue = int.MinValue;
                for (int i = 0; i < s.P.Sleeve.Count; i++)
                {
                    var withCard = new List<SolverCard>(s.P.Table) { s.P.Sleeve[i] };
                    int value = Total(withCard, s.PTarget) + s.PMods;
                    if (value <= s.PlainTarget && value > bestValue)
                    {
                        bestValue = value;
                        bestIndex = i;
                    }
                }
                if (bestIndex >= 0)
                {
                    return new SolverMove { Kind = MoveKind.PlaySleeve, SleeveIndex = bestIndex };
                }
            }

            return new SolverMove { Kind = MoveKind.Pass };
        }

        // ---------------------------------------------------------------- reporting

        /// <summary>Human readable label for a move against a specific root state.</summary>
        internal static string MoveLabel(SolverMove move, SolverState s)
        {
            switch (move.Kind)
            {
                case MoveKind.PlayTop:
                    return "play top card " + Label(s.P.Top) + (move.ToOpponent ? " to opponent" : "");
                case MoveKind.SleeveTop:
                    return "sleeve top card " + Label(s.P.Top);
                case MoveKind.PlaySleeve:
                    SolverCard card = move.SleeveIndex >= 0 && move.SleeveIndex < s.P.Sleeve.Count
                        ? s.P.Sleeve[move.SleeveIndex]
                        : null;
                    return "play sleeve card " + Label(card) + (move.ToOpponent ? " to opponent" : "");
                default:
                    return "pass";
            }
        }

        private static string Label(SolverCard card)
        {
            return card == null ? "?" : card.Label;
        }

        private static string Format(float v)
        {
            return v.ToString("+0.##;-0.##;0");
        }

        /// <summary>
        /// Multi-line report of every legal root move with its evaluated value plus the
        /// actions that are unavailable, with the exact reason. Used by the diagnostics dump.
        /// </summary>
        internal string DescribeMoves(SolverState root, List<MoveEvaluation> evaluations)
        {
            var sb = new StringBuilder();
            List<MoveState> legal = EnumerateMoves(root);
            for (int i = 0; i < legal.Count; i++)
            {
                sb.Append("  ").Append(MoveLabel(legal[i].Move, root));
                if (i < evaluations.Count)
                {
                    sb.Append(" -> ").Append(Format(evaluations[i].Value));
                }
                else
                {
                    sb.Append(" -> not evaluated");
                }
                sb.AppendLine();
            }

            if (!CanPass(root))
            {
                sb.AppendLine("  pass -> illegal (Supper: only allowed when the table has no free slot)");
            }
            if (root.P.Top == null)
            {
                sb.AppendLine("  play top / sleeve top -> unavailable (draw pile empty)");
            }
            if (root.P.Capacity <= 0)
            {
                sb.AppendLine("  play to own table -> unavailable (table full)");
            }
            if (root.P.Top != null && root.P.Top.CanPlayOpponent && root.O.Capacity <= 0)
            {
                sb.AppendLine("  play to opponent -> unavailable (opponent table full)");
            }
            if (!CanSleeve(root))
            {
                string why;
                if (root.SleeveSize <= 0)
                {
                    why = "sleeve size 0";
                }
                else if (root.P.Top == null)
                {
                    why = "draw pile empty";
                }
                else if (root.Payable < SleeveCost(root))
                {
                    why = $"cannot pay (payable {root.Payable} < cost {SleeveCost(root)})";
                }
                else
                {
                    why = "unavailable";
                }
                sb.Append("  sleeve top -> unavailable (").Append(why).AppendLine(")");
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- memo keys

        private string Key(char phase, SolverState s)
        {
            _sb.Clear();
            _sb.Append(phase).Append('|');
            AppendSide(s.P);
            _sb.Append('#');
            AppendSide(s.O);
            _sb.Append('#');
            _sb.Append(s.InsightLeft).Append(',').Append(s.Payable);
            return _sb.ToString();
        }

        private void AppendSide(SolverSide side)
        {
            _sb.Append(side.DeckPos).Append(',')
                .Append(side.Passed ? 1 : 0).Append(',')
                .Append(side.OutOfCards ? 1 : 0).Append(',')
                .Append(side.Capacity).Append(',')
                .Append(side.SleeveDraws).Append(',')
                .Append(side.Bet).Append(',');

            for (int i = 0; i < side.Sleeve.Count; i++)
            {
                if (i > 0)
                {
                    _sb.Append('+');
                }
                _sb.Append(side.Sleeve[i].Sig);
            }
            _sb.Append(',');

            _sortScratch.Clear();
            _sortScratch.AddRange(side.Table);
            _sortScratch.Sort((a, b) => string.CompareOrdinal(a.Sig, b.Sig));
            for (int i = 0; i < _sortScratch.Count; i++)
            {
                if (i > 0)
                {
                    _sb.Append('+');
                }
                _sb.Append(_sortScratch[i].Sig);
            }
        }
    }
}
