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

    /// <summary>One card as it appears in a trace step; only values and types matter.</summary>
    internal sealed class TraceCard
    {
        public int[] Values;
        public int[] Types;
        public uint Flags;

        public string Label => "[" + string.Join("/", Values) + "]";
    }

    /// <summary>One action of the expected line of play behind the chosen move.</summary>
    internal sealed class SolveTraceStep
    {
        /// <summary>0 player move, 1 opponent draw, 2 opponent pass, 3 resolve.</summary>
        public int Kind;
        public SolverMove Move;
        public TraceCard Card;

        /// <summary>Only set on resolve steps: -1 loss, 0 tie, 1 win.</summary>
        public int Winner;

        /// <summary>Table values after the step.</summary>
        public int PValue;
        public int OValue;

        /// <summary>Bets after the step; effects can move coins into them.</summary>
        public int PBet;
        public int OBet;

        /// <summary>Stashes after the step.</summary>
        public int PStash;
        public int OStash;
    }

    /// <summary>Everything one native solve call reports back.</summary>
    internal sealed class SolveResult
    {
        public SolverMove Best;
        public float Value;
        public int Nodes;
        public bool Aborted;

        /// <summary>True when only some root moves were evaluated before the budget ran out.</summary>
        public bool Partial;

        public bool ProgressTieBreak;

        /// <summary>True when an effect loop forced the search to score a line at its depth cap.</summary>
        public bool DepthCapped;

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

        /// <summary>Legal root moves in canonical order.</summary>
        public List<SolverMove> Legal;

        /// <summary>One entry per evaluated root move; <see cref="MoveEvaluation.Index"/> names the move.</summary>
        public List<MoveEvaluation> Evaluations;

        /// <summary>Expected line of play behind <see cref="Best"/>; empty on budget aborts.</summary>
        public List<SolveTraceStep> Trace;
    }

    /// <summary>Human readable rendering of solver results for logs and the status line.</summary>
    internal static class SolverReport
    {
        internal const string ProgressNote = "progress tie-break: played a card instead of passing";
        internal const string DepthNote = "search cap: an endless effect loop was cut off at a horizon";

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
                MoveEvaluation evaluation = FindEvaluation(result, i);
                if (evaluation != null)
                {
                    sb.Append(" -> ").Append(Format(evaluation.Value));
                }
                else
                {
                    sb.Append(" -> not evaluated");
                }
                sb.AppendLine();
            }

            if (result.Partial)
            {
                sb.AppendLine("  note: budget ran out; values cover completed root moves only");
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

        private static MoveEvaluation FindEvaluation(SolveResult result, int moveIndex)
        {
            foreach (MoveEvaluation evaluation in result.Evaluations)
            {
                if (evaluation.Index == moveIndex)
                {
                    return evaluation;
                }
            }
            return null;
        }

        /// <summary>
        /// The expected line of play after the chosen move, one line per action. Empty when
        /// the search had to abort before it could replay the line.
        /// </summary>
        internal static string DescribeTrace(SolveResult result)
        {
            var sb = new StringBuilder();
            int pValue = 0, oValue = 0, pBet = 0, oBet = 0, pStash = 0, oStash = 0;
            bool first = true;
            foreach (SolveTraceStep step in result.Trace)
            {
                switch (step.Kind)
                {
                    case 0:
                        sb.Append("  you ");
                        if (step.Move.Kind == MoveKind.SleeveTop)
                        {
                            sb.Append("sleeve ");
                        }
                        else if (step.Move.Kind == MoveKind.PlaySleeve)
                        {
                            sb.Append("play sleeve ");
                        }
                        else if (step.Move.Kind == MoveKind.PlayTop)
                        {
                            sb.Append("play top ");
                        }
                        else
                        {
                            sb.Append("pass");
                        }
                        if (step.Card != null)
                        {
                            sb.Append(step.Card.Label);
                        }
                        if (step.Move.ToOpponent)
                        {
                            sb.Append(" to opponent");
                        }
                        break;
                    case 1:
                        sb.Append("  opponent draws ").Append(step.Card == null ? "?" : step.Card.Label);
                        break;
                    case 2:
                        sb.Append("  opponent passes");
                        break;
                    default:
                        sb.Append("  resolve: ")
                            .Append(step.Winner > 0 ? "win" : step.Winner < 0 ? "loss" : "tie")
                            .Append(" (").Append(step.PValue).Append(" vs ").Append(step.OValue).Append(")");
                        break;
                }

                // Effect-driven changes would be invisible otherwise: coins can move into a
                // bet and table values can change at resolution without a player move.
                var parts = new List<string>();
                if (step.Kind != 3 && (first || step.PValue != pValue || step.OValue != oValue))
                {
                    parts.Add($"P {step.PValue} vs O {step.OValue}");
                }
                if (first || step.PBet != pBet || step.OBet != oBet)
                {
                    parts.Add($"bet {step.PBet}/{step.OBet}");
                }
                if (!first && (step.PStash != pStash || step.OStash != oStash))
                {
                    parts.Add($"coins {step.PStash}/{step.OStash}");
                }
                if (parts.Count > 0)
                {
                    sb.Append(" (").Append(string.Join(", ", parts)).Append(')');
                }
                sb.AppendLine();

                pValue = step.PValue;
                oValue = step.OValue;
                pBet = step.PBet;
                oBet = step.OBet;
                pStash = step.PStash;
                oStash = step.OStash;
                first = false;
            }
            return sb.ToString();
        }

        private static string Label(SolverCard card)
        {
            return card == null ? "?" : card.Label;
        }
    }
}
