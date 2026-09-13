# Card effect modeling

The native solver runs the whole draw phase as a deterministic game, so it has to know
what each played card does. Effects are read from the live `CardEffectsContainer` objects
on every capture (Unity cannot serialize `[SerializeReference]` effect data offline) and
reduced to a flat record per effect; `EffectsMapper.cs` is the only writer, `effects.rs`
the only interpreter. Every semantic below mirrors one decompiled behavior; approximations
are called out explicitly.

## What is modeled

| Trigger | When it runs |
| --- | --- |
| `Play` | the played card's own effects, after `CardPlayedToTable` reactions |
| `CardPlayedToTable` | reactions of cards already on the table (same/other table filter) |
| `ResolutionBeforeCount` / `ResolutionAfterCount` | at round settling, before the winner is compared |

Triggers that never fire inside a draw phase (`EndOfDraw`, `EndOfRound`,
`DevourSatiated`) are dropped. Containers that are not `Forced` count as optional; the
search activates the player's optional containers (the prompt is answered by
`AutoActivateOptionalEffects`) and the opponent's, except when the opponent has a
blackjack and the card sets `DontActivateIfOpponentHasBlackjack`.

Activation conditions are evaluated for `HasBlackJack`, `CanRaise`, constant-only
`Coins` and `Blind`; any other condition (e.g. `CardAdjacency`, which needs card
identity) marks the whole container unmodeled.

### Ops

| Op | Classes | Notes |
| --- | --- | --- |
| Break / Mend | `ApplyBroken`, `ApplyMend` | negate/restore eligible values, add/strip the `Broken` type |
| AddValue / SetValue | `ModifyCardValue` | relative/absolute value change; `Random` card choice is unmodeled |
| Invert | `InvertValue` | ports the game's source-card quirk (see below) |
| Drain | `Drain` | zeroes the target's first value, adds it to the source's `Drain`-typed value |
| Hollow | `GainHollow` | sets the hollow flag (capacity refund) |
| Discard / Exhaust | `Discard`, `Exhaust`, `AnglerFishTrap` | move cards out of the table/sleeve/draw pile |
| Move | `MoveCard` | first/last/all selection; deck/discard/sleeve/table destinations |
| Draw | `DrawCards` | draw pile to table (capacity-gated), sleeve, discard or deck |
| Duplicate | `Duplicate` | ToTable and ToSleeve copies; ToDeck's random insert is unmodeled |
| Exploit | `Exploit` | opponent stash to opponent bet, value-limited |
| Coins | `CoinsEffect`, `RaiseEffect` | create/destroy/move between stash, bet and winners pot |
| Trigger | `TriggerCardEffects` | runs another trigger on the target cards |
| Swap | `Swap` | first target area vs the source card |
| SkipTurn | `SkipTurn` | no-op inside a round (`SkipTurns` is consumed at round start) |
| Insight | `Insight` | opponent gains peek draws; the player side is a no-op (see below) |
| Ignite | `Ignite` | marks targets; a second mark burns (exhausts) the card |
| Mark | `MarkCard`, `FamilyTrioGraphVersion` | pure VFX/UI, modeled as no-ops |

### Targeting

- Relative positions (`CardPosition.ERelativeCardPosition`) resolve by table placement
  order: left/right neighbours, opposite slot, opposite plus adjacents, any-range,
  self/on-top.
- Gather steps (`GatherCardsByLocation`) resolve owner bits (player/opponent/self/other)
  and locations (table, draw pile, discard pile, sleeve — sleeve only exists for the
  player). At most one location gather plus one relative gather per effect.
  `UniversalCardTargetConfig.ExcludeSourceCard` drops the source card from the gathered
  list; the legacy relative positions are not affected.
- `CardFilterSet.TakeNum` first/last is packed into the record; random takes are
  unmodeled. `IsBrokenCardFilter` keeps cards with a `Broken` value, but is unmodeled
  when the gather includes the discard pile (only its count is known). Any other filter
  marks the effect unmodeled.
- `CardTargetConfiguration` is supported when it only selects by owner/location (no
  face/ace/value removal).

### Unmodeled effects

Anything not listed above — `ShuffleDeck`, `Demand`, `PlayCard`, `QueensGift`,
`RotateCards`, `Transform`, `InsertCards`, `GainCardValue`, `Purge`, the trio/trap
events, non-constant coin/amount collectors, random choices, and so on — is skipped.
The plugin logs each distinct card/effect once, with the card name and the reason the
mapping failed so it can be prioritized:

```
Unmodeled card effect: GameCard_Flames_9_Upgraded: InsertCards (unsupported effect) (the search treats it as a no-op).
```

A card with more than four modeled effects is treated as effect-free for the same
reason: partial modeling would silently score the card wrong, and the log line names the
card.

## Approximations

These are deliberate reductions; each would need game observation or a much larger state
space to remove:

- **Table geometry.** The game picks the opponent's slot randomly, so exact positions
  are unrecoverable. Relative targets therefore use placement order, and the executor's
  own drop is always the first free slot. Angler Fish Trap's "card placed opposite"
  reaction consequently fires on any other-table placement rather than only a true
  opposite-slot one.
- **Temp modifiers.** The game replaces other temporary modifiers on a card when one is
  applied; the solver applies adjustments to the current values and clamps per the
  `` `Broken` `` type. `AddValue` on a positive value that would exceed the target is
  still evaluated as the game evaluates it.
- **`InvertValue`.** Ported as decompiled: the modifier lands on the *source* card N
  times, and the type clamp turns every value into zero.
- **Deck insertions.** `Duplicate.ToDeck` inserts at a random position, so it is
  unmodeled; `MoveCard` to the draw pile uses the top of the pile.
- **Reshuffles.** Drawing from an empty draw pile stops — the game would shuffle the
  discard pile in a random order first. `Discard`'s "discard from a draw pile, then
  shuffle the discard back in" is modeled as the removal only, which matches until the
  pile would empty.
- **Insight.** The player already sees every card and reorders the pile through the
  plugin's insight dialog, so player-side `Insight` is a no-op in the search; the
  opponent's `Insight` adds peek draws to its draw policy. `ForceDiscardAce` and
  `InsightOnOpposer` are unmodeled.
- **Discard-pile contents.** Only the count is known; effects that need to take specific
  cards from the discard pile are unmodeled.
- **Face-down cards.** Treated as revealed (the reveal effects themselves are modeled
  only as far as they change values).
- **Execution gates.** The game's `ShouldExecute` / `CanExecute` checks (for example
  `ModifyCardValue` refusing to raise a table past the target) are not evaluated: an
  effect that is mapped always applies. The per-effect `CanExecute` conditions that do
  change the outcome are covered as far as the activation conditions above go.
- **Settling order.** `ResolutionBeforeCount` and `ResolutionAfterCount` run before the
  winner is compared; the game may count between them.
- **Endless effect loops.** A hollow card that duplicates itself back into the sleeve can
  be played forever, so the game tree is not finite in general. The search bounds every
  line by recursion depth and by player decisions without draw-pile progress; at the cap
  the line is scored at the current resolution, and the decision log marks the result
  with a "search cap" note. Legitimate rounds finish far below both caps.
- **Ignite burn reactions.** The burn removes the card; effects that react to an
  `Ignited` card gaining a modifier are not modeled (none exist inside the draw phase as
  decompiled).

## Data flow

```
GameCard.CardEffectContainers
  -> EffectsMapper.Map          (managed, main thread at capture time)
  -> SolverEffect[per card]     (flat records, protocol v3)
  -> effects.rs apply_play / apply_trigger
  -> State mutations            (values, types, tables, coins) during the search
```

Each search runs on a dedicated native thread with a large stack, and the trace in the
decision log replays the expected line with those mutations applied. Every trace step
carries the table values, bets and stashes right after it, so effect-driven changes show
up even without a player move:

```
Expected line:
  you play top [7] (P 21 vs O 22, bet 2/4)
  opponent passes
  you play top [11/1] (P 21 vs O 22, bet 2/4)
  ...
  resolve: win (21 vs 22) (bet 4/4)
```
