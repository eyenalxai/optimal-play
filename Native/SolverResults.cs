using System.Collections.Generic;
using System.Text;

namespace BlackJacket.OptimalPlay
{
    /// <summary>Why "sleeve top card" is not a legal move; used by the diagnostics dump.</summary>
    internal enum SleeveReason
    {
        Available,
        SizeZero,
        DrawPileEmpty,
        CannotPay,
        Unavailable,
    }

    /// <summary>Everything one native solve call reports back.</summary>
    internal sealed class SolveResult
    {
        public SolverMove Best;
        public float Value;
        public int Nodes;
        public bool Aborted;
        public bool ProgressTieBreak;

        public int PValue;
        public int OValue;
        public int PTarget;
        public int OTarget;
        public int Payable;
        public int SleeveCost;

        public bool CanPass;
        public bool CanSleeve;
        public bool TopNull;
        public bool OwnTableFull;
        public bool OpponentTableFull;
        public SleeveReason SleeveReason;

        /// <summary>Legal root moves in preference order; evaluations are its prefix.</summary>
        public List<SolverMove> Legal;

        public List<MoveEvaluation> Evaluations;
    }

    /// <summary>Human readable rendering of solver results for logs and the status line.</summary>
    internal static class SolverReport
    {
        internal const string ProgressNote = "progress tie-break: played a dead card instead of passing";

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

        internal static string Format(float value)
        {
            return value.ToString("+0.##;-0.##;0");
        }

        /// <summary>
        /// Multi-line report of every legal root move with its evaluated value plus the
        /// actions that are unavailable, with the exact reason. Used by the diagnostics dump.
        /// </summary>
        internal static string DescribeMoves(SolverState root, SolveResult result)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < result.Legal.Count; i++)
            {
                sb.Append("  ").Append(MoveLabel(result.Legal[i], root));
                if (i < result.Evaluations.Count)
                {
                    sb.Append(" -> ").Append(Format(result.Evaluations[i].Value));
                }
                else
                {
                    sb.Append(" -> not evaluated");
                }
                sb.AppendLine();
            }

            if (!result.CanPass)
            {
                sb.AppendLine("  pass -> illegal (Supper: only allowed when the table has no free slot)");
            }
            if (result.TopNull)
            {
                sb.AppendLine("  play top / sleeve top -> unavailable (draw pile empty)");
            }
            if (result.OwnTableFull)
            {
                sb.AppendLine("  play to own table -> unavailable (table full)");
            }
            if (result.OpponentTableFull)
            {
                sb.AppendLine("  play to opponent -> unavailable (opponent table full)");
            }
            if (!result.CanSleeve)
            {
                string why;
                switch (result.SleeveReason)
                {
                    case SleeveReason.SizeZero:
                        why = "sleeve size 0";
                        break;
                    case SleeveReason.DrawPileEmpty:
                        why = "draw pile empty";
                        break;
                    case SleeveReason.CannotPay:
                        why = $"cannot pay (payable {result.Payable} < cost {result.SleeveCost})";
                        break;
                    default:
                        why = "unavailable";
                        break;
                }
                sb.Append("  sleeve top -> unavailable (").Append(why).AppendLine(")");
            }
            return sb.ToString();
        }

        private static string Label(SolverCard card)
        {
            return card == null ? "?" : card.Label;
        }
    }
}
