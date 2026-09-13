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

        // Activation conditions.
        internal const byte CondNone = 0;
        internal const byte CondHasBlackjack = 1;
        internal const byte CondCoins = 2;
        internal const byte CondCanRaise = 3;

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
                    unmodeled.Add($"trigger {(int)container.Trigger}: unsupported activation conditions");
                    continue;
                }

                CardEffect[] effects = container.Effects;
                if (effects == null)
                {
                    continue;
                }
                foreach (CardEffect effect in effects)
                {
                    List<SolverEffect> specs = MapEffect(effect);
                    if (specs == null)
                    {
                        unmodeled.Add(effect == null ? "empty effect" : effect.GetType().Name);
                        continue;
                    }
                    foreach (SolverEffect spec in specs)
                    {
                        spec.Trigger = trigger;
                        spec.Flags = flags;
                        spec.Cond = cond;
                        spec.CondCmp = condCmp;
                        spec.CondA = condA;
                        spec.CondB = condB;
                        mapped.Add(spec);
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
                default:
                    return false;
            }
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
        }

        /// <summary>
        /// Combine the target sources an effect class can carry (relative card positions,
        /// a universal gather config and a simple card target config). Returns false when
        /// the combination cannot be represented by the native target kinds.
        /// </summary>
        private static bool TryTargets(int relative, UniversalCardTargetConfig universal,
            CardTargetConfiguration config, out TargetSpec spec)
        {
            spec = null;
            int owner = 0;
            int location = 0;
            bool hasGather = false;
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
                            return false;
                    }
                }
                if (universal.ExcludeSourceCard || !TryFilter(universal.CardFilterSet, location, out filter))
                {
                    return false;
                }
            }

            if (config != null && !IsEmptyTargetConfig(config))
            {
                if (hasGather)
                {
                    // Two independent gather sources in one effect: not representable.
                    return false;
                }
                if (config.RemoveFaces != EFace.None || config.RemoveAces || config.RemoveNonFaces)
                {
                    return false;
                }
                owner = (int)config.Owner;
                location = (int)config.Location;
                hasGather = true;
            }

            if (!hasGather && relative == 0)
            {
                return false;
            }

            spec = new TargetSpec
            {
                Relative = relative,
                Owner = owner,
                Location = location,
                Filter = filter,
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
            if (set.Filters.Length != 1 || set.Filters[0] is not CardFilterSet.TakeNum take)
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
        private static List<SolverEffect> MapEffect(CardEffect effect)
        {
            switch (effect)
            {
                case ApplyBroken broken:
                {
                    TargetSpec spec;
                    if (!TryTargets(EnumInt(broken, "CardPositions"), null, null, out spec))
                    {
                        return null;
                    }
                    return One(WithTarget(OpBreak, spec));
                }
                case ApplyMend mend:
                {
                    TargetSpec spec;
                    if (!TryTargets(EnumInt(mend, "CardPositions"),
                            Field<UniversalCardTargetConfig>(mend, "_universalCardTargetConfig"), null, out spec))
                    {
                        return null;
                    }
                    return One(WithTarget(OpMend, spec));
                }
                case ModifyCardValue modify:
                {
                    // Random card choices cannot be modeled deterministically.
                    if (EnumInt(modify, "cardChoiceMode") != 1)
                    {
                        return null;
                    }
                    TargetSpec spec;
                    if (!TryTargets(EnumInt(modify, "RelativeCardPosition"),
                            Field<UniversalCardTargetConfig>(modify, "_targetConfig"),
                            Field<CardTargetConfiguration>(modify, "TargetConfiguration"), out spec))
                    {
                        return null;
                    }
                    SolverEffect mapped = WithTarget(EnumInt(modify, "modifierMode") == 1 ? OpSetValue : OpAddValue, spec);
                    mapped.A = 0; // ETargetValues.All
                    mapped.B = IntField(modify, "Value");
                    return One(mapped);
                }
                case InvertValue invert:
                {
                    TargetSpec spec;
                    if (!TryTargets(EnumInt(invert, "RelativeCardPosition"), null, null, out spec))
                    {
                        return null;
                    }
                    return One(WithTarget(OpInvert, spec));
                }
                case Drain drain:
                {
                    TargetSpec spec;
                    if (!TryTargets(EnumInt(drain, "_relativeCardTargets"), null,
                            Field<CardTargetConfiguration>(drain, "_cardTargetingConfig"), out spec))
                    {
                        return null;
                    }
                    return One(WithTarget(OpDrain, spec));
                }
                case GainHollow hollow:
                {
                    TargetSpec spec;
                    if (!TryTargets(EnumInt(hollow, "_cardPosition"), null, null, out spec))
                    {
                        return null;
                    }
                    return One(WithTarget(OpHollow, spec));
                }
                case Discard discard:
                {
                    // Discarding a draw pile also shuffles the discard into it, which the
                    // native search cannot model (the result depends on a random order).
                    if (HasGatherLocation(discard.CardTargetConfig, (int)ELocation.DrawPile))
                    {
                        return null;
                    }
                    TargetSpec spec;
                    if (!TryTargets((int)discard.RelativeCardPosition, discard.CardTargetConfig, null, out spec))
                    {
                        return null;
                    }
                    return One(WithTarget(OpDiscard, spec));
                }
                case Exhaust exhaust:
                {
                    TargetSpec spec;
                    if (!TryTargets(0, Field<UniversalCardTargetConfig>(exhaust, "_targetConfig"), null, out spec))
                    {
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
                        return null;
                    }
                    int sourceLocation = (int)source.Location;
                    // Discard pile contents are not modeled, so it cannot be a source.
                    if ((sourceLocation & (int)ELocation.DiscardPile) != 0 || sourceLocation == 0)
                    {
                        return null;
                    }
                    int mode = EnumInt(move, "_mode");
                    if (mode == 2)
                    {
                        return null; // Random selection
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
                        return null; // ToDeck inserts at a random position
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
                        return null;
                    }
                    int sourceZone = (int)coins.SourceZone;
                    int targetZone = (int)coins.TargetZone;
                    if (!SingleZone(sourceZone) || !SingleZone(targetZone))
                    {
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
                    TargetSpec spec;
                    if (!TryTargets((int)trigger.RelativeCardPosition, null, trigger.CardTargetConfiguration, out spec))
                    {
                        return null;
                    }
                    SolverEffect mapped = WithTarget(OpTrigger, spec);
                    mapped.A = (int)trigger.Trigger;
                    return One(mapped);
                }
                case Swap swap:
                {
                    TargetSpec spec;
                    if (!TryTargets(EnumInt(swap, "TargetCardPosition"), null, null, out spec))
                    {
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
                case MarkCard:
                    // Purely cosmetic (card back / mark VFX).
                    return One(new SolverEffect { Op = OpNone, Target = TargetNone });
                default:
                    return null;
            }
        }

        private static List<SolverEffect> One(SolverEffect effect)
        {
            return new List<SolverEffect> { effect };
        }

        private static bool HasGatherLocation(UniversalCardTargetConfig universal, int location)
        {
            if (universal == null || universal.GatherCardsSteps == null)
            {
                return false;
            }
            foreach (GatherCardsStep step in universal.GatherCardsSteps)
            {
                if (step is GatherCardsByLocation byLocation && ((int)byLocation.Location & location) != 0)
                {
                    return true;
                }
            }
            return false;
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
