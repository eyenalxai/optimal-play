using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace BlackJacket.OptimalPlay
{
    /// <summary>
    /// One card effect reduced to the flat record the native solver interprets. The field
    /// layout is documented in <c>native/src/protocol.rs</c>; the constants below mirror
    /// <c>native/src/effects.rs</c> and must move together with it.
    /// </summary>
    internal sealed class SolverEffect
    {
        public byte Trigger;
        public byte Flags;
        public byte Op;
        public byte Target;
        public int T1;
        public int T2;
        public int T3;
        public int A;
        public int B;
        public int C;
        public int D;
        public uint Filter;
        public byte Cond;
        public byte CondCmp;
        public int CondA;
        public int CondB;
    }

    /// <summary>
    /// Maps live <see cref="CardEffect"/> containers to solver descriptors. The native
    /// search only models the deterministic core of the game's effects; every effect that
    /// cannot be represented is reported by name so the plugin log shows exactly which
    /// cards the search treats as if they had no effect.
    /// </summary>
    internal static class EffectsMapper
    {
        // CardEffectsTrigger.
        internal const byte TriggerPlay = 10;
        internal const byte TriggerResolutionBefore = 20;
        internal const byte TriggerResolutionAfter = 21;
        internal const byte TriggerCardPlayedToTable = 60;

        // Effect ops; keep in sync with native/src/effects.rs.
        internal const byte OpNone = 0;
        internal const byte OpBreak = 1;
        internal const byte OpMend = 2;
        internal const byte OpAddValue = 3;
        internal const byte OpSetValue = 4;
        internal const byte OpInvert = 5;
        internal const byte OpDrain = 6;
        internal const byte OpHollow = 7;
        internal const byte OpDiscard = 8;
        internal const byte OpExhaust = 9;
        internal const byte OpMove = 10;
        internal const byte OpDraw = 11;
        internal const byte OpDuplicate = 12;
        internal const byte OpExploit = 13;
        internal const byte OpCoins = 14;
        internal const byte OpSkipTurn = 15;
        internal const byte OpTrigger = 16;
        internal const byte OpSwap = 17;
        internal const byte OpInsight = 18;
        internal const byte OpIgnite = 19;

        // Target kinds.
        internal const byte TargetNone = 0;
        internal const byte TargetRelative = 1;
        internal const byte TargetGather = 2;
        internal const byte TargetRelativeAndGather = 3;

        // `CardPosition.ERelativeCardPosition` bits the mapper emits directly.
        internal const int RelOpposite = 0x4;
        internal const int RelSelf = 0x10;

        // Effect flags.
        internal const byte FlagOptional = 1;
        internal const byte FlagSuppressOnBlackjack = 2;
        internal const byte FlagReactSameTable = 4;
        internal const byte FlagReactOtherTable = 8;
        internal const byte FlagExcludeSource = 16;

        // Activation conditions.
        internal const byte CondNone = 0;
        internal const byte CondHasBlackjack = 1;
        internal const byte CondCoins = 2;
        internal const byte CondCanRaise = 3;
        internal const byte CondBlind = 4;

        // CardLocationConfig destinations used by MoveCard/DrawCards.
        internal const int MoveTopOfDeck = 1;
        internal const int MoveBottomOfDeck = 2;
        internal const int MoveDiscard = 3;
        internal const int MoveSleeve = 4;
        internal const int MoveTable = 5;

        /// <summary>
        /// Every effect the solver models for this card, in container order. Triggers the
        /// search never reaches (EndOfDraw, EndOfRound, DevourSatiated) are dropped silently;
        /// anything else that cannot be represented is appended to <paramref name="unmodeled"/>.
        /// </summary>
        internal static List<SolverEffect> Map(GameCard card, List<string> unmodeled)
        {
            var mapped = new List<SolverEffect>();
            CardEffectsContainer[] containers = card.CardEffectContainers;
            if (containers == null)
            {
                return mapped;
            }

            foreach (CardEffectsContainer container in containers)
            {
                byte trigger = (byte)container.Trigger;
                if (trigger != TriggerPlay && trigger != TriggerResolutionBefore
                    && trigger != TriggerResolutionAfter && trigger != TriggerCardPlayedToTable)
                {
                    continue;
                }

                byte flags = 0;
                if (!container.Forced)
                {
                    flags |= FlagOptional;
                }
                if (container.Trigger == CardEffectsTrigger.CardPlayedToTable)
                {
                    switch (EnumInt(container, "_tableSide"))
                    {
                        case 0:
                            flags |= FlagReactSameTable;
                            break;
                        case 1:
                            flags |= FlagReactOtherTable;
                            break;
                        default:
                            flags |= FlagReactSameTable | FlagReactOtherTable;
                            break;
                    }
                }
                if (card.DontActivateIfOpponentHasBlackjack)
                {
                    flags |= FlagSuppressOnBlackjack;
                }

                if (!TryConditions(container.ActivationConditions, out byte cond, out byte condCmp, out int condA, out int condB))
                {
                    unmodeled.Add($"{card.name}: unsupported conditions {ConditionNames(container.ActivationConditions)}");
                    continue;
                }

                CardEffect[] effects = container.Effects;
                if (effects == null)
                {
                    continue;
                }
                foreach (CardEffect effect in effects)
                {
                    List<SolverEffect> specs = MapEffect(effect, out string reason);
                    if (specs == null)
                    {
                        unmodeled.Add($"{card.name}: {effect?.GetType().Name ?? "empty effect"} ({reason})");
                        continue;
                    }
                    Assign(specs, trigger, flags, cond, condCmp, condA, condB, mapped);
                    // An Angler Fish Trap also listens for a card being placed in its
                    // opposite slot and runs its whole effect again. The opposite slot
                    // lives on the other table.
                    if (effect is AnglerFishTrap && trigger != TriggerCardPlayedToTable)
                    {
                        List<SolverEffect> reaction = MapEffect(effect, out _);
                        if (reaction != null)
                        {
                            Assign(reaction, TriggerCardPlayedToTable, FlagReactOtherTable, 0, 0, 0, 0, mapped);
                        }
                    }
                }
            }

            // The descriptor arrays in a card are fixed size; a card with more modeled
            // effects than fit is treated as effect-free rather than partially modeled.
            if (mapped.Count > MaxEffectsPerCard)
            {
                unmodeled.Add($"{card.name}: {mapped.Count} effects exceed the native limit");
                mapped.Clear();
            }
            return mapped;
        }

        internal const int MaxEffectsPerCard = 4;

        /// <summary>
        /// The activation conditions the native `condition_ok` knows. A container with more
        /// than one condition (or an unsupported one) is reported unmodeled.
        /// </summary>
        private static bool TryConditions(ActivationConditions conditions, out byte cond, out byte cmp, out int a, out int b)
        {
            cond = CondNone;
            cmp = 0;
            a = 0;
            b = 0;
            if (conditions == null || conditions.Conditions == null || conditions.Conditions.Length == 0)
            {
                return true;
            }
            if (conditions.Conditions.Length != 1)
            {
                return false;
            }

            switch (conditions.Conditions[0])
            {
                case HasBlackJack hasBlackJack:
                    cond = CondHasBlackjack;
                    a = hasBlackJack.ExecutorHasBlackJack ? 1 : 0;
                    return true;
                case CanRaise canRaise:
                    cond = CondCanRaise;
                    a = canRaise.CoinAmount;
                    return true;
                case Coins coins when IsEmptyCollector(coins.AddToCoinAmount) && IsEmptyCollector(coins.AddToCoinAmount2):
                    cond = CondCoins;
                    cmp = (byte)coins.ComparisonMode;
                    a = coins.CoinAmount;
                    b = coins.CoinAmount2;
                    return true;
                case Blind blind:
                    cond = CondBlind;
                    cmp = (byte)blind.ComparisonMode;
                    a = blind.Value;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Names of a container's activation conditions, for the log.</summary>
        private static string ConditionNames(ActivationConditions conditions)
        {
            if (conditions == null || conditions.Conditions == null || conditions.Conditions.Length == 0)
            {
                return "none";
            }
            var names = new List<string>(conditions.Conditions.Length);
            foreach (ActivationCondition condition in conditions.Conditions)
            {
                names.Add(condition == null ? "null" : condition.GetType().Name);
            }
            return string.Join("+", names);
        }

        /// <summary>True when a coin collector contributes nothing at execution time.</summary>
        private static bool IsEmptyCollector(CoinCollectorConfig collector)
        {
            return collector == null || ((int)collector.Owners == 0 && (int)collector.Zones == 0);
        }

        /// <summary>True when a `CardTargetConfiguration` selects nothing.</summary>
        private static bool IsEmptyTargetConfig(CardTargetConfiguration config)
        {
            return config == null
                || ((int)config.Owner == 0 && (int)config.Location == 0 && (int)config.RemoveFaces == 0
                    && !config.RemoveAces && !config.RemoveNonFaces && config.RemoveLowerThan == -1
                    && config.RemoveHigherThan == -1
                    && (config.RemoveCardValues == null || config.RemoveCardValues.Length == 0));
        }

        /// <summary>Resolved target selection, ready to be copied into descriptors.</summary>
        private sealed class TargetSpec
        {
            public int Relative;
            public int Owner;
            public int Location;
            public uint Filter;
            public bool ExcludeSource;
        }

        /// <summary>
        /// Combine the target sources an effect class can carry (relative card positions,
        /// a universal gather config and a simple card target config). Returns false when
        /// the combination cannot be represented by the native target kinds; `error` then
        /// names the reason for the unmodeled log.
        /// </summary>
        private static bool TryTargets(int relative, UniversalCardTargetConfig universal,
            CardTargetConfiguration config, out TargetSpec spec, out string error)
        {
            spec = null;
            error = null;
            int owner = 0;
            int location = 0;
            bool hasGather = false;
            bool excludeSource = false;
            uint filter = 0;

            if (universal != null && universal.GatherCardsSteps != null && universal.GatherCardsSteps.Length > 0)
            {
                foreach (GatherCardsStep step in universal.GatherCardsSteps)
                {
                    switch (step)
                    {
                        case GatherCardsByLocation byLocation:
                            owner |= (int)byLocation.Owner;
                            location |= (int)byLocation.Location;
                            hasGather = true;
                            break;
                        case GatherCardsByRelativeLocation byRelative:
                            relative |= (int)byRelative.RelativePosition;
                            break;
                        default:
                            error = $"gather step {step.GetType().Name}";
                            return false;
                    }
                }
                if (!TryFilter(universal.CardFilterSet, location, out filter))
                {
                    error = "card filter set";
                    return false;
                }
                excludeSource = universal.ExcludeSourceCard;
            }

            if (config != null && !IsEmptyTargetConfig(config))
            {
                if (hasGather)
                {
                    // Two independent gather sources in one effect: not representable.
                    error = "two gather sources";
                    return false;
                }
                if (config.RemoveFaces != EFace.None || config.RemoveAces || config.RemoveNonFaces)
                {
                    error = "face/ace filter";
                    return false;
                }
                owner = (int)config.Owner;
                location = (int)config.Location;
                hasGather = true;
            }

            if (!hasGather && relative == 0)
            {
                error = "no targets";
                return false;
            }

            spec = new TargetSpec
            {
                Relative = relative,
                Owner = owner,
                Location = location,
                Filter = filter,
                ExcludeSource = excludeSource,
            };
            return true;
        }

        private static bool TryFilter(CardFilterSet set, int location, out uint filter)
        {
            filter = 0;
            if (set == null || set.Filters == null || set.Filters.Length == 0)
            {
                return true;
            }
            if (set.Filters.Length != 1)
            {
                return false;
            }
            if (set.Filters[0] is CardFilterSet.IsBrokenCardFilter)
            {
                // Only the count of a discard pile is modeled, so a content filter cannot
                // look at it.
                if ((location & (int)ELocation.DiscardPile) != 0)
                {
                    return false;
                }
                filter = 2u;
                return true;
            }
            if (set.Filters[0] is not CardFilterSet.TakeNum take)
            {
                return false;
            }
            int mode = EnumInt(take, "_order");
            int count = IntField(take, "_num");
            // The game enumerates a draw pile bottom-first while the solver starts at the
            // top, so first/last flip for draw-pile-only gathers.
            bool drawOnly = (location & (int)ELocation.DrawPile) != 0
                && (location & ((int)ELocation.Table | (int)ELocation.DiscardPile | (int)ELocation.Sleeve)) == 0;
            if (drawOnly)
            {
                mode = 1 - mode;
            }
            if (mode < 0 || mode > 1 || count < 0 || count > 0xFFF)
            {
                return false;
            }
            filter = 1u | ((uint)mode << 4) | ((uint)count << 6);
            return true;
        }

        private static SolverEffect WithTarget(byte op, TargetSpec spec)
        {
            var effect = new SolverEffect { Op = op, Filter = spec.Filter };
            if (spec.ExcludeSource)
            {
                effect.Flags |= FlagExcludeSource;
            }
            if (spec.Owner != 0)
            {
                if (spec.Relative != 0)
                {
                    effect.Target = TargetRelativeAndGather;
                    effect.T1 = spec.Relative;
                    effect.T2 = spec.Owner;
                    effect.T3 = spec.Location;
                }
                else
                {
                    effect.Target = TargetGather;
                    effect.T1 = spec.Owner;
                    effect.T2 = spec.Location;
                }
            }
            else
            {
                effect.Target = TargetRelative;
                effect.T1 = spec.Relative;
            }
            return effect;
        }

        /// <summary>
        /// Map one effect instance to descriptors; null when the solver cannot model it.
        /// A few effects expand into more than one descriptor (Angler Fish Trap).
        /// </summary>
        private static List<SolverEffect> MapEffect(CardEffect effect, out string reason)
        {
            reason = null;
            switch (effect)
            {
                case ApplyBroken broken:
                {
                    if (!TryTargets(EnumInt(broken, "CardPositions"), null, null,
                            out TargetSpec spec, out string error))
                    {
                        reason = error;
                        return null;
                    }
                    return One(WithTarget(OpBreak, spec));
                }
                case ApplyMend mend:
                {
                    if (!TryTargets(EnumInt(mend, "CardPositions"),
                            Field<UniversalCardTargetConfig>(mend, "_universalCardTargetConfig"), null,
                            out TargetSpec spec, out string error))
                    {
                        reason = error;
                        return null;
                    }
                    return One(WithTarget(OpMend, spec));
                }
                case ModifyCardValue modify:
                {
                    // Random card choices cannot be modeled deterministically.
                    if (EnumInt(modify, "cardChoiceMode") != 1)
                    {
                        reason = "random card choice";
                        return null;
                    }
                    if (!TryTargets(EnumInt(modify, "RelativeCardPosition"),
                            Field<UniversalCardTargetConfig>(modify, "_targetConfig"),
                            Field<CardTargetConfiguration>(modify, "TargetConfiguration"),
                            out TargetSpec spec, out string error))
                    {
                        reason = error;
                        return null;
                    }
                    SolverEffect mapped = WithTarget(EnumInt(modify, "modifierMode") == 1 ? OpSetValue : OpAddValue, spec);
                    mapped.A = 0; // ETargetValues.All
                    mapped.B = IntField(modify, "Value");
                    return One(mapped);
                }
                case InvertValue invert:
                {
                    if (!TryTargets(EnumInt(invert, "RelativeCardPosition"), null, null,
                            out TargetSpec spec, out string error))
                    {
                        reason = error;
                        return null;
                    }
                    return One(WithTarget(OpInvert, spec));
                }
                case Drain drain:
                {
                    if (!TryTargets(EnumInt(drain, "_relativeCardTargets"), null,
                            Field<CardTargetConfiguration>(drain, "_cardTargetingConfig"),
                            out TargetSpec spec, out string error))
                    {
                        reason = error;
                        return null;
                    }
                    return One(WithTarget(OpDrain, spec));
                }
                case GainHollow hollow:
                {
                    if (!TryTargets(EnumInt(hollow, "_cardPosition"), null, null,
                            out TargetSpec spec, out string error))
                    {
                        reason = error;
                        return null;
                    }
                    return One(WithTarget(OpHollow, spec));
                }
                case Discard discard:
                {
                    // Discarding cards from a draw pile also shuffles the discard back in
                    // when the pile empties. The search cannot model the random reshuffle,
                    // but removing the discarded cards from the pile is the right short-term
                    // behavior and the search stops at draw-pile exhaustion anyway.
                    if (!TryTargets((int)discard.RelativeCardPosition, discard.CardTargetConfig, null,
                            out TargetSpec spec, out string error))
                    {
                        reason = error;
                        return null;
                    }
                    return One(WithTarget(OpDiscard, spec));
                }
                case Exhaust exhaust:
                {
                    if (!TryTargets(0, Field<UniversalCardTargetConfig>(exhaust, "_targetConfig"), null,
                            out TargetSpec spec, out string error))
                    {
                        reason = error;
                        return null;
                    }
                    return One(WithTarget(OpExhaust, spec));
                }
                case MoveCard move:
                {
                    CardLocationConfig source = Field<CardLocationConfig>(move, "_sourceConfig");
                    CardLocationConfig target = Field<CardLocationConfig>(move, "_targetConfig");
                    if (source == null || target == null)
                    {
                        reason = "missing move location";
                        return null;
                    }
                    int sourceLocation = (int)source.Location;
                    // Discard pile contents are not modeled, so it cannot be a source.
                    if ((sourceLocation & (int)ELocation.DiscardPile) != 0 || sourceLocation == 0)
                    {
                        reason = "discard pile source";
                        return null;
                    }
                    int mode = EnumInt(move, "_mode");
                    if (mode == 2)
                    {
                        reason = "random selection";
                        return null;
                    }
                    int destination;
                    if (((int)target.Location & (int)ELocation.DrawPile) != 0)
                    {
                        destination = MoveTopOfDeck;
                    }
                    else if (((int)target.Location & (int)ELocation.DiscardPile) != 0)
                    {
                        destination = MoveDiscard;
                    }
                    else if (((int)target.Location & (int)ELocation.Sleeve) != 0)
                    {
                        destination = MoveSleeve;
                    }
                    else if (((int)target.Location & (int)ELocation.Table) != 0)
                    {
                        destination = MoveTable;
                    }
                    else
                    {
                        reason = "unknown move destination";
                        return null;
                    }

                    var mapped = new SolverEffect
                    {
                        Op = OpMove,
                        Target = TargetGather,
                        T1 = (int)source.Owner,
                        T2 = sourceLocation,
                    };
                    if (mode != 3)
                    {
                        mapped.Filter = 1u | ((uint)mode << 4) | ((uint)IntField(move, "_amount") << 6);
                    }
                    mapped.A = destination;
                    mapped.B = (int)target.Owner;
                    return One(mapped);
                }
                case DrawCards draw:
                {
                    CardLocationConfig target = Field<CardLocationConfig>(draw, "_targetLocation");
                    if (target == null || (int)target.Location == 0)
                    {
                        reason = "missing draw location";
                        return null;
                    }
                    var mapped = new SolverEffect
                    {
                        Op = OpDraw,
                        Target = TargetNone,
                        A = (int)Field<EOwner>(draw, "_sourceDrawPile"),
                        B = (int)target.Owner,
                        C = (int)target.Location,
                        D = IntField(draw, "_cardAmount"),
                    };
                    return One(mapped);
                }
                case Duplicate duplicate:
                {
                    int mode = EnumInt(duplicate, "Mode");
                    if (mode == 1)
                    {
                        reason = "random deck position";
                        return null;
                    }
                    var mapped = new SolverEffect
                    {
                        Op = OpDuplicate,
                        Target = TargetNone,
                        A = mode + 1,
                        B = IntField(duplicate, "Count"),
                    };
                    return One(mapped);
                }
                case Exploit exploit:
                {
                    if (!IsEmptyAmountCollector(exploit.amountCollector))
                    {
                        reason = "amount collector";
                        return null;
                    }
                    return One(new SolverEffect { Op = OpExploit, Target = TargetNone, A = exploit.Amount });
                }
                case CoinsEffect coins:
                {
                    if (coins.CardTargetingMode != CoinsEffect.ECardTargetingMode.None
                        || !IsEmptyCollector(coins.AddAmount)
                        || !IsEmptyTargetConfig(coins.AddAmountByCards))
                    {
                        reason = "coin collector targets cards";
                        return null;
                    }
                    int sourceZone = (int)coins.SourceZone;
                    int targetZone = (int)coins.TargetZone;
                    if (!SingleZone(sourceZone) || !SingleZone(targetZone))
                    {
                        reason = "multi-zone coin move";
                        return null;
                    }
                    if (sourceZone == 0 && targetZone == 0)
                    {
                        return One(new SolverEffect { Op = OpNone, Target = TargetNone });
                    }
                    return One(new SolverEffect
                    {
                        Op = OpCoins,
                        Target = TargetNone,
                        A = (int)coins.SourceOwner | (sourceZone << 8),
                        B = (int)coins.TargetOwner | (targetZone << 8),
                        C = coins.Amount,
                    });
                }
                case Effects.SkipTurn:
                    // `SkipTurns` is consumed at round start; inside a round it does nothing.
                    return One(new SolverEffect { Op = OpSkipTurn, Target = TargetNone });
                case TriggerCardEffects trigger:
                {
                    if (!TryTargets((int)trigger.RelativeCardPosition, null, trigger.CardTargetConfiguration,
                            out TargetSpec spec, out string error))
                    {
                        reason = error;
                        return null;
                    }
                    SolverEffect mapped = WithTarget(OpTrigger, spec);
                    mapped.A = (int)trigger.Trigger;
                    return One(mapped);
                }
                case Swap swap:
                {
                    if (!TryTargets(EnumInt(swap, "TargetCardPosition"), null, null,
                            out TargetSpec spec, out string error))
                    {
                        reason = error;
                        return null;
                    }
                    return One(WithTarget(OpSwap, spec));
                }
                case AnglerFishTrap:
                {
                    // Exhausts every opposite card and then itself.
                    return new List<SolverEffect>
                    {
                        WithTarget(OpExhaust, new TargetSpec { Relative = RelOpposite }),
                        WithTarget(OpExhaust, new TargetSpec { Relative = RelSelf }),
                    };
                }
                case Ignite ignite:
                {
                    if (!TryTargets(EnumInt(ignite, "RelativeTarget"), null,
                            Field<CardTargetConfiguration>(ignite, "_targetConfiguration"),
                            out TargetSpec spec, out string error))
                    {
                        reason = error;
                        return null;
                    }
                    return One(WithTarget(OpIgnite, spec));
                }
                case Insight insight:
                {
                    // Forcing the ace out or aiming at the opponent's deck changes which cards
                    // get reordered; the simple case only grants the executor insight.
                    if (insight.ForceDiscardAce || insight.InsightOnOpposer
                        || !IsEmptyAmountCollector(insight.AmountCollector))
                    {
                        reason = insight.ForceDiscardAce ? "forces the ace"
                            : insight.InsightOnOpposer ? "opponent deck insight"
                            : "amount collector";
                        return null;
                    }
                    return One(new SolverEffect
                    {
                        Op = OpInsight,
                        Target = TargetNone,
                        A = insight.InsightAmount,
                    });
                }
                case RaiseEffect raise:
                {
                    if (!IsEmptyAmountCollector(raise.amountCollector))
                    {
                        reason = "amount collector";
                        return null;
                    }
                    // Greed turns the raise into a forced raise paid from the other side.
                    bool greed = GameController.Instance != null
                        && GameController.Instance.CurrentMatch != null
                        && GameController.Instance.CurrentMatch.GameRules.Contains(GameRule.Greed);
                    int owner = (int)(greed ? CoinZoneHelper.EOwner.Other : CoinZoneHelper.EOwner.Self);
                    return One(new SolverEffect
                    {
                        Op = OpCoins,
                        Target = TargetNone,
                        A = owner | ((int)CoinZoneHelper.EZone.Stash << 8),
                        B = owner | ((int)CoinZoneHelper.EZone.Bet << 8),
                        C = raise.Amount,
                    });
                }
                case FamilyTrioGraphVersion:
                    // Only raises the FamilyTrio UI event; no card, coin or value changes.
                    return One(new SolverEffect { Op = OpNone, Target = TargetNone });
                case MarkCard:
                    // Purely cosmetic (card back / mark VFX).
                    return One(new SolverEffect { Op = OpNone, Target = TargetNone });
                default:
                    reason = "unsupported effect";
                    return null;
            }
        }

        private static List<SolverEffect> One(SolverEffect effect)
        {
            return new List<SolverEffect> { effect };
        }

        /// <summary>Copy container-level trigger, flags and conditions onto mapped specs.</summary>
        private static void Assign(List<SolverEffect> specs, byte trigger, byte flags, byte cond,
            byte condCmp, int condA, int condB, List<SolverEffect> target)
        {
            foreach (SolverEffect spec in specs)
            {
                spec.Trigger = trigger;
                spec.Flags = flags;
                spec.Cond = cond;
                spec.CondCmp = condCmp;
                spec.CondA = condA;
                spec.CondB = condB;
                target.Add(spec);
            }
        }

        private static bool SingleZone(int zone)
        {
            return (zone & (zone - 1)) == 0;
        }

        private static bool IsEmptyAmountCollector(AmountCollector collector)
        {
            return collector == null
                || (IsEmptyCollector(collector.CoinCollectorConfig)
                    && IsEmptyTargetConfig(collector.CardTargetingConfiguration)
                    && (collector.SlotCollectorConfig == null || (int)collector.SlotCollectorConfig.SlotType == 0));
        }

        private static int EnumInt(object target, string name)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    object raw = field.GetValue(target);
                    return raw == null ? 0 : Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                }
                // Some effects expose their settings as auto-properties.
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.CanRead)
                {
                    object raw = property.GetValue(target);
                    return raw == null ? 0 : Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                }
            }
            return 0;
        }

        private static int IntField(object target, string name)
        {
            return EnumInt(target, name);
        }

        private static T Field<T>(object target, string name)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    object raw = field.GetValue(target);
                    return raw is T typed ? typed : default;
                }
            }
            return default;
        }
    }
}
