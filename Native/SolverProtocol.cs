using System;
using System.Collections.Generic;

namespace BlackJacket.OptimalPlay
{
    /// <summary>
    /// Writer for the flat little-endian buffer protocol the native solver reads.
    ///
    /// The layout is documented in <c>native/src/protocol.rs</c>; both sides must move
    /// together and <see cref="Version"/> is bumped whenever the layout changes, so a
    /// mismatched pair fails loudly instead of misreading memory.
    /// </summary>
    internal static class SolverProtocol
    {
        internal const uint Version = 3;

        internal const uint FlagUprising = 1;
        internal const uint FlagSupper = 2;

        internal const uint CardAce = 1 << 0;
        internal const uint CardHollow = 1 << 1;
        internal const uint CardAlwaysInsight = 1 << 2;
        internal const uint CardCanPlayOpponent = 1 << 3;
        internal const uint CardBroken = 1 << 4;

        // `ModifiableValue.EType` bits; the native search keys value behavior off these.
        internal const uint CardTypeTarget = 1;
        internal const uint CardTypeDrain = 4;
        internal const uint CardTypeBroken = 8;

        internal const uint OpSetDeckOrder = 1;
        internal const uint OpTakeDeckToSleeve = 2;
        internal const uint OpMoveTableToSleeve = 3;

        /// <summary>Side index used by the candidate ops.</summary>
        internal const int SidePlayer = 0;
        internal const int SideOpponent = 1;

        internal const int ErrVersion = -1;
        internal const int ErrTruncated = -2;
        internal const int ErrCard = -3;
        internal const int ErrOp = -4;
        internal const int ErrPanic = -100;

        /// <summary>Serialize a captured state as a standalone solve/evaluate input buffer.</summary>
        internal static byte[] WriteState(SolverState state)
        {
            var w = new SolverBuffer();
            WriteState(w, state);
            return w.ToArray();
        }

        internal static void WriteState(SolverBuffer w, SolverState state)
        {
            w.U32(Version);
            w.U32((state.Uprising ? FlagUprising : 0) | (state.Supper ? FlagSupper : 0));
            w.I32(state.PlainTarget);
            w.I32(state.PMods);
            w.I32(state.OMods);
            w.I32(state.HoldsAt);
            w.I32(state.InsightLeft);
            w.I32(state.SleeveSize);
            w.I32(state.Payable);
            w.I32(state.Pot);
            w.U32(state.PotUsable ? 1u : 0u);
            w.I32(state.Blind);

            int[] costs = state.SleeveCosts ?? Array.Empty<int>();
            w.U32(costs.Length);
            foreach (int cost in costs)
            {
                w.I32(cost);
            }

            WriteSide(w, state.P);
            WriteSide(w, state.O);
        }

        private static void WriteSide(SolverBuffer w, SolverSide side)
        {
            w.I32(side.DeckPos);
            w.I32(side.Capacity);
            w.I32(side.Bet);
            w.I32(side.Stash);
            w.U32(side.SleeveDraws);
            w.U32(side.Passed ? 1u : 0u);
            // The game never tells us the opponent ran out of cards; the native search tracks
            // that itself while it advances the draw pile.
            w.U32(0u);
            // Only the size of the discard pile matters: the solver never sees its contents.
            w.U32(side.DiscardCount);
            WriteZone(w, side.Deck);
            WriteZone(w, side.Sleeve);
            WriteZone(w, side.Table);
        }

        private static void WriteZone(SolverBuffer w, List<SolverCard> cards)
        {
            w.U32(cards.Count);
            foreach (SolverCard card in cards)
            {
                WriteCard(w, card);
            }
        }

        private static void WriteCard(SolverBuffer w, SolverCard card)
        {
            int[] values = card.Values ?? Array.Empty<int>();
            w.U32(values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                w.I32(values[i]);
                int type = card.Types != null && i < card.Types.Length ? card.Types[i] : (int)CardTypeTarget;
                w.U32((uint)type);
            }
            uint flags = 0;
            if (card.IsAce)
            {
                flags |= CardAce;
            }
            if (card.IsHollow)
            {
                flags |= CardHollow;
            }
            if (card.AlwaysInsight)
            {
                flags |= CardAlwaysInsight;
            }
            if (card.CanPlayOpponent)
            {
                flags |= CardCanPlayOpponent;
            }
            if (card.Broken)
            {
                flags |= CardBroken;
            }
            w.U32(flags);
            w.U32(card.Ignited);

            List<SolverEffect> effects = card.Effects ?? EmptyEffects;
            w.U32(effects.Count);
            foreach (SolverEffect effect in effects)
            {
                w.U32(effect.Trigger);
                w.U32(effect.Flags);
                w.U32(effect.Op);
                w.U32(effect.Target);
                w.I32(effect.T1);
                w.I32(effect.T2);
                w.I32(effect.T3);
                w.I32(effect.A);
                w.I32(effect.B);
                w.I32(effect.C);
                w.I32(effect.D);
                w.U32(effect.Filter);
                w.U32(effect.Cond);
                w.U32(effect.CondCmp);
                w.I32(effect.CondA);
                w.I32(effect.CondB);
            }
        }

        private static readonly List<SolverEffect> EmptyEffects = new List<SolverEffect>();

        /// <summary>Append `u32 candidate_count`; call between it and the candidates the batch writer helpers.</summary>
        internal static void WriteCandidateCount(SolverBuffer w, int count)
        {
            w.U32(count);
        }

        /// <summary>Append one candidate whose ops are written by the callback.</summary>
        internal static void WriteCandidate(SolverBuffer w, Action<SolverBuffer> writeOps)
        {
            var ops = new SolverBuffer();
            writeOps(ops);
            w.U32(ops.Count);
            w.Append(ops);
        }

        internal static void WriteSetDeckOrder(SolverBuffer w, int side, int[] order)
        {
            w.U32(OpSetDeckOrder);
            w.U32(side);
            w.I32(0);
            w.I32(0);
            w.U32(order.Length);
            foreach (int index in order)
            {
                w.U32(index);
            }
        }

        internal static void WriteTakeDeckToSleeve(SolverBuffer w, int side, int deckIndex)
        {
            w.U32(OpTakeDeckToSleeve);
            w.U32(side);
            w.I32(deckIndex);
            w.I32(0);
            w.U32(0);
        }

        internal static void WriteMoveTableToSleeve(SolverBuffer w, int side, int tableIndex)
        {
            w.U32(OpMoveTableToSleeve);
            w.U32(side);
            w.I32(tableIndex);
            w.I32(0);
            w.U32(0);
        }
    }

    /// <summary>Growable little-endian byte writer used for protocol buffers.</summary>
    internal sealed class SolverBuffer
    {
        private readonly List<byte> _bytes = new List<byte>(256);

        internal int Count => _bytes.Count;

        internal void U32(uint value)
        {
            _bytes.Add((byte)value);
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)(value >> 16));
            _bytes.Add((byte)(value >> 24));
        }

        internal void U32(int value)
        {
            U32(unchecked((uint)value));
        }

        internal void I32(int value)
        {
            U32(unchecked((uint)value));
        }

        internal void U64(ulong value)
        {
            U32((uint)value);
            U32((uint)(value >> 32));
        }

        internal void F64(double value)
        {
            U64(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
        }

        internal void Append(SolverBuffer other)
        {
            _bytes.AddRange(other._bytes);
        }

        internal byte[] ToArray()
        {
            return _bytes.ToArray();
        }
    }
}
