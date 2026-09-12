using System.Collections.Generic;
using System.Text;

namespace BlackJacket.OptimalPlay
{
    /// <summary>
    /// Pure helpers for the insight reorder dialog.
    ///
    /// The dialog shows the top N cards of a draw pile. The player orders them on a line and
    /// may move the deck placeholder between them: every card to the left of the placeholder
    /// is buried below the rest of the deck, every card to the right stays on top. Within
    /// each group the line reads bottom-to-top (left = deeper).
    /// </summary>
    internal static class InsightPlanner
    {
        /// <summary>All permutations of the shown cards; only the first <paramref name="maxPermuted"/> are permuted for large N.</summary>
        public static List<List<SolverCard>> Permutations(List<SolverCard> cards, int maxPermuted = 4)
        {
            var result = new List<List<SolverCard>>();
            if (cards.Count <= maxPermuted)
            {
                PermuteInto(cards, 0, result);
                return result;
            }

            List<SolverCard> head = cards.GetRange(0, maxPermuted);
            List<SolverCard> tail = cards.GetRange(maxPermuted, cards.Count - maxPermuted);
            var headPermutations = new List<List<SolverCard>>();
            PermuteInto(head, 0, headPermutations);
            foreach (List<SolverCard> permutation in headPermutations)
            {
                var full = new List<SolverCard>(permutation.Count + tail.Count);
                full.AddRange(permutation);
                full.AddRange(tail);
                result.Add(full);
            }
            return result;
        }

        private static void PermuteInto(List<SolverCard> cards, int index, List<List<SolverCard>> result)
        {
            if (index == cards.Count)
            {
                result.Add(new List<SolverCard>(cards));
                return;
            }
            for (int i = index; i < cards.Count; i++)
            {
                Swap(cards, index, i);
                PermuteInto(cards, index + 1, result);
                Swap(cards, index, i);
            }
        }

        private static void Swap(List<SolverCard> list, int a, int b)
        {
            SolverCard tmp = list[a];
            list[a] = list[b];
            list[b] = tmp;
        }

        /// <summary>Final deck order (top-first) for a permutation split around the unchanged middle of the deck.</summary>
        public static List<SolverCard> CandidateOrder(List<SolverCard> permutation, int split, List<SolverCard> rest)
        {
            var order = new List<SolverCard>(permutation.Count + rest.Count);
            for (int i = 0; i < split; i++)
            {
                order.Add(permutation[i]);
            }
            order.AddRange(rest);
            for (int i = split; i < permutation.Count; i++)
            {
                order.Add(permutation[i]);
            }
            return order;
        }

        /// <summary>Line order (left-to-right = bottom-to-top) that realizes <see cref="CandidateOrder"/> in the dialog.</summary>
        public static List<SolverCard> DisplayLine(List<SolverCard> permutation, int split)
        {
            var line = new List<SolverCard>(permutation.Count);
            for (int i = permutation.Count - 1; i >= split; i--)
            {
                line.Add(permutation[i]);
            }
            for (int i = split - 1; i >= 0; i--)
            {
                line.Add(permutation[i]);
            }
            return line;
        }

        public static string OrderKey(List<SolverCard> order)
        {
            var sb = new StringBuilder(order.Count * 6);
            for (int i = 0; i < order.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('|');
                }
                sb.Append(order[i].Sig);
            }
            return sb.ToString();
        }
    }
}
