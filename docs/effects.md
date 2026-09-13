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

Activation conditions are evaluated for `HasBlackJack`, `CanRaise` and constant-only
`Coins`; any other condition marks the whole container unmodeled.

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
| Coins | `CoinsEffect` | create/destroy/move between stash, bet and winners pot |
| Trigger | `TriggerCardEffects` | runs another trigger on the target cards |
| Swap | `Swap` | first target area vs the source card |
| SkipTurn | `SkipTurn` | no-op inside a round (`SkipTurns` is consumed at round start) |
| Mark | `MarkCard` | pure VFX, modeled as a no-op |

### Targeting

- Relative positions (`CardPosition.ERelativeCardPosition`) resolve by table placement
  order: left/right neighbours, opposite slot, opposite plus adjacents, any-range,
  self/on-top.
- Gather steps (`GatherCardsByLocation`) resolve owner bits (player/opponent/self/other)
  and locations (table, draw pile, discard pile, sleeve — sleeve only exists for the
  player). At most one location gather plus one relative gather per effect.
- `CardFilterSet.TakeNum` first/last is packed into the record; random takes are
  unmodeled. Other filters mark the effect unmodeled.
- `CardTargetConfiguration` is supported when it only selects by owner/location (no
  face/ace/value removal).

### Unmodeled effects

Anything not listed above — `ShuffleDeck`, `Demand`, `PlayCard`, `QueensGift`,
`RotateCards`, `Transform`, `InsertCards`, `GainCardValue`, `Purge`, the trio/trap
events, non-constant coin/amount collectors, random choices, and so on — is skipped.
The plugin logs each distinct one once:

```
Unmodeled card effect: ShuffleDeck (the search treats it as a no-op).
```

A card with more than four modeled effects is treated as effect-free for the same
reason: partial modeling would silently score the card wrong, and the log line names the
card.

## Approximations

These are deliberate reductions; each would need game observation or a much larger state
space to remove:

- **Table geometry.** The game picks the opponent's slot randomly, so exact positions
  are unrecoverable. Relative targets therefore use placement order, and the executor's
  own drop is always the first free slot.
- **Temp modifiers.** The game replaces other temporary modifiers on a card when one is
  applied; the solver applies adjustments to the current values and clamps per the
  `` `Broken` `` type. `AddValue` on a positive value that would exceed the target is
  still evaluated as the game evaluates it.
- **`InvertValue`.** Ported as decompiled: the modifier lands on the *source* card N
  times, and the type clamp turns every value into zero.
- **Deck insertions.** `Duplicate.ToDeck` inserts at a random position, so it is
  unmodeled; `MoveCard` to the draw pile uses the top of the pile.
- **Reshuffles.** Drawing from an empty draw pile stops — the game would shuffle the
  discard pile in a random order first.
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

## Data flow

```
GameCard.CardEffectContainers
  -> EffectsMapper.Map          (managed, main thread at capture time)
  -> SolverEffect[per card]     (flat records, protocol v2)
  -> effects.rs apply_play / apply_trigger
  -> State mutations            (values, types, tables, coins) during the search
```

The trace in the decision log replays the expected line with those mutations applied, so
the logged resolve line shows the post-effect totals the search used.
