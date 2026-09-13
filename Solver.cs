using System;
using System.Collections.Generic;
using System.Linq;

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
    /// A card reduced to the information that matters for the value game. Built on the
    /// main thread from a <see cref="Card3D"/> and handed to the native solver.
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
                // Own copy: the captured state is handed to the solver worker and may
                // outlive the frame; game effects must not be able to mutate it.
                Values = (int[])values.Clone(),
                IsAce = gc.Id == "ace",
                Highest = gc.HighestValue,
                IsHollow = gc.IsHollow,
                AlwaysInsight = gc.OpponentAlwaysHasInsightOnThisCard,
                CanPlayOpponent = gc.CanBePlayedInOpponentsSlots,
                Broken = values.Any(v => v < 0),
                Name = name,
                Effect = string.IsNullOrEmpty(gc.EffectText) ? null : gc.EffectText.Replace("\n", " "),
            };
        }
    }

    /// <summary>One side of the table as captured for the solver.</summary>
    internal sealed class SolverSide
    {
        /// <summary>Draw pile top first: Deck[DeckPos] is drawn next.</summary>
        public List<SolverCard> Deck;

        /// <summary>Only the size of the discard pile matters to the solver (reshuffle check).</summary>
        public int DiscardCount;

        public int DeckPos;

        public List<SolverCard> Sleeve = new List<SolverCard>();
        public List<SolverCard> Table = new List<SolverCard>();

        public bool Passed;
        public int Capacity;
        public int Bet;
        public int SleeveDraws;

        public SolverCard Top => DeckPos < Deck.Count ? Deck[DeckPos] : null;
    }

    /// <summary>
    /// Everything the native solver needs about one draw phase position. Captured on the
    /// Unity main thread and handed to the solver worker; nothing mutates it afterwards.
    /// </summary>
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
    }
}
