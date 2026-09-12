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

        public string Label
        {
            get
            {
                switch (Kind)
                {
                    case MoveKind.PlayTop: return "play top card";
                    case MoveKind.SleeveTop: return "sleeve top card";
                    case MoveKind.PlaySleeve: return "play sleeve card";
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
        public string Sig;

        public static SolverCard From(Card3D card)
        {
            GameCard gc = card.GameCard;
            int[] values = gc.Values;
            return new SolverCard
            {
                Values = values,
                IsAce = gc.Id == "ace",
                Highest = gc.HighestValue,
                IsHollow = gc.IsHollow,
                AlwaysInsight = gc.OpponentAlwaysHasInsightOnThisCard,
                Sig = (gc.Id == "ace" ? "a" : "n") + ":" + string.Join("|", values),
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

        public int PTarget => PlainTarget + PMods - (Uprising ? P.Capacity * 2 : 0);
        public int OTarget => PlainTarget + OMods - (Uprising ? O.Capacity * 2 : 0);

        public int PValue => RoundSolver.Total(P.Table, PTarget) + PMods;
        public int OValue => RoundSolver.Total(O.Table, OTarget) + OMods;
        public bool PBusted => PValue > PlainTarget;
        public bool OBusted => OValue > PlainTarget;
        public bool PBJ => RoundSolver.HasBlackJack(P.Table);
        public bool OBJ => RoundSolver.HasBlackJack(O.Table);

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

        public SolverMove BestMove(SolverState root, out float value, out int nodes, out bool aborted,
            out List<MoveEvaluation> evaluations)
        {
            _memo = new Dictionary<string, float>();
            _nodes = 0;
            _aborted = false;
            _startMs = Environment.TickCount;
            evaluations = new List<MoveEvaluation>();

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
            }
            return best;
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
                moves.Add(new MoveState { Move = new SolverMove { Kind = MoveKind.PlayTop }, State = n });
            }

            if (s.P.Capacity > 0)
            {
                for (int i = 0; i < s.P.Sleeve.Count; i++)
                {
                    SolverCard card = s.P.Sleeve[i];
                    SolverState n = s.Clone();
                    n.P.Sleeve.RemoveAt(i);
                    n.P.Table.Add(card);
                    n.P.Capacity--;
                    if (card.IsHollow)
                    {
                        n.P.Capacity++;
                    }
                    moves.Add(new MoveState
                    {
                        Move = new SolverMove { Kind = MoveKind.PlaySleeve, SleeveIndex = i },
                        State = n,
                    });
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

        internal static int Total(List<SolverCard> cards, int target)
        {
            var sums = new HashSet<int> { 0 };
            foreach (SolverCard card in cards)
            {
                var next = new HashSet<int>();
                foreach (int sum in sums)
                {
                    foreach (int value in card.Values)
                    {
                        next.Add(sum + value);
                    }
                }
                sums = next;
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
            return (a.IsAce && b.Values.Any(v => v == 10))
                || (b.IsAce && a.Values.Any(v => v == 10));
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
