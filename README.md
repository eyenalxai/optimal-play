# Black Jacket - Optimal Play

A BepInEx 5 plugin that plays the draw phase of every match for you, as well as it can
possibly be played.

## How it works

The plugin reads **both draw piles in order** (plus the sleeves, discards, tables, slot
counts, coin zones and the opponent's AI parameters), so the rest of the round is a
deterministic game. It then runs a full backward induction over every legal move:

- play the top card of your draw pile to the table,
- move the top card to your sleeve (and play sleeve cards later),
- pass (or unpass where the game allows it),

while the opponent is simulated with the game's own deterministic policy
(`WillDrawAnotherCard`, holds-at threshold, deck insight, pass/unpass checks, slot
capacity). The move with the best coin outcome is chosen:

```
win  -> + opponent's bet
loss -> - your bet (blind + sleeve costs, exactly like the game books them)
tie  ->  0
```

The search itself lives in a native Rust library (`OptimalPlaySolver.dll`) and runs on a
background thread with the solver budget from the config, so the draw phase never stalls
on a search and hard positions can get a full second of thought. Candidate sets (insight
arrangements, demand picks, card choices, shuffle samples) are evaluated in parallel.
The managed plugin only captures the position, submits the job and executes the answer.

Because card order is known, the bot can do things a human cannot: it knows when the
opponent is about to bust, when a card is safe to sleeve, and when standing pat already
wins the round. It also understands blackjack (two-card 21), busts, the single-card
tiebreak, the `Uprising` target reduction and the `Supper` pass restriction.

The game is re-read and re-solved on every one of your turns, so card effects that change
values on the fly are picked up as soon as they happen. The search also applies the card
effects themselves: reactions, breaks and mends, drains, movement, duplication, coin
effects and more are part of the game tree, not just the current values. See
[docs/effects.md](docs/effects.md) for exactly which effects are modeled, which are not
(they are logged by name) and the documented approximations.

## Automated selections

The same solver answers the blocking choice screens:

- **Insight** – reorders the revealed top cards of your deck (or of the opponent's deck)
  to the arrangement with the best round outcome. Every arrangement the dialog allows is
  scored, including burying cards below the rest of the deck with the placeholder.
- **Demand** – takes the revealed card with the best round outcome into your sleeve
  (free of charge) or skips when taking is worse than skipping.
- **Card choice** – when an effect such as the awakened Greed 3 (*"Sleeve a card from your
  slots."*) makes you pick one of your own table cards, sleeves the candidate whose removal
  leaves the best round outcome (usually a dead or least useful card).
- **Shuffle** – when a shuffle effect asks which deck to shuffle, samples both options and
  picks the one with the better expected outcome.

Optional card-effect prompts are answered with `AutoActivateOptionalEffects`.

## Configuration

`BepInEx/config/com.blackjacket.mods.optimalplay.cfg`

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Enable the mod. |
| `AutoPlay` | `true` | Play automatically; toggle at runtime with `ToggleKey`. |
| `ToggleKey` | `F8` | Toggles AutoPlay. |
| `ActionDelay` | `0.5` | Seconds between actions, so animations/effects can finish. |
| `AutoActivateOptionalEffects` | `true` | Answer optional card-effect prompts with Activate (else Skip). |
| `AutoSelectInsight` | `true` | Reorder insight windows to the best arrangement. |
| `AutoSelectDemand` | `true` | Take (or skip) the best card in demand windows. |
| `AutoSelectCardChoice` | `true` | Pick the best card when an effect asks you to select one of your table cards (e.g. "Sleeve a card from your slots"). |
| `AutoSelectShuffle` | `true` | Choose which deck to shuffle. |
| `LogDecisions` | `true` | Write every decision with its evaluation to the log. |
| `LogState` | `false` | Write a detailed state dump (both sides, cards, legal and illegal moves) for every decision. |
| `DumpStateKey` | `F9` | Log the current position and full move analysis once, without acting. |
| `VerboseKey` | `F10` | Toggle `LogState` at runtime. |
| `ShowStatus` | `true` | Small status line showing the current decision. |
| `SearchNodeBudget` | `2000000` | Maximum search nodes per decision (safety cap; searches stop early when solved). |
| `SearchTimeMs` | `1000` | Maximum search time per decision, in milliseconds. Searches run on a background thread. |

## Reading the log

`BepInEx/LogOutput.log` gets one line per decision when `LogDecisions` is on:

```
Decision: play top card Hearts_7_Upgraded[-7] | P 0 vs O 5 | ev -2 | 632 nodes | options: ...
```

The `options:` list is every root move with what it is worth (in coins, same scale as the
round result above). Cards are printed as `Name[values]`. `(budget reached: best completed
move)` means the search ran out of time/nodes before evaluating every root move; the
chosen move is the best one it fully completed. `(budget reached, greedy fallback)` means
it could not complete even one and used the quick heuristic. When effects are modeled the
log adds the expected line of play:

```
Expected line:
  you play top [5] to opponent
  opponent draws [10]
  opponent passes
  resolve: loss (18 vs 24)
```

Selection dialogs log one line each, e.g.:

```
Card choice: sleeving Hearts_1_Upgraded[-1] (value +2; options: Hearts_1_Upgraded[-1]=+2, Copper_5[5]=-2).
```

For the full picture press `F9` at any time (also outside your turn): it writes the whole
position (`match`, both sides, table, next cards in both draw piles, sleeve, discard), every
legal move with its value, and every unavailable move with the reason:

```
=== Optimal Play: F9 dump ===
match: target 21, holds at 17, rules: none
player: value 0, target 21, capacity 3, passed no, bet 2, sleeve draws 0/2, next sleeve cost 1, payable 3
  table: (empty)
  deck top: Hearts_7_Upgraded[-7] (Exploit 2.), Hearts_10[10], ...
  discard: 12 cards
  sleeve: Hearts_9_Upgraded[-9] (Mend each surrounding card.), Hearts_3_Upgraded[-3/-1]
moves:
  pass -> -2
  play top card Hearts_7_Upgraded[-7] -> -2
  sleeve top card Hearts_7_Upgraded[-7] -> -3
result: pass | ev -2 | 632 nodes (complete)
```

`F10` toggles these dumps for every decision at runtime, so you do not have to restart the
game after changing `LogState`.

## Negative cards and dead cards

Some cards really are worth negative numbers, by design - awakened Hearts, for example
(the game itself says: *"Hearts have negative values when awakened, and can break other
cards."*). A negative card's `values` in the log/solver are the game's current values, and
the solver plays them exactly like the game scores them.

Two rules keep those cards from stalling the bot:

- **Progress tie-break.** Losing a round costs your bet no matter how you lose it. When every
  move (including passing) is worth the same non-positive amount and the top card is a dead
  card (best value <= 0), the bot plays/sleeves it instead of passing. Passing forever would
  leave that card stuck on top of the draw pile and never reach the rest of the deck.
- **Playing to the opponent's table.** Cards with *"Play into any slot."* (the game's
  `CanBePlayedInOpponentsSlots`) can be dropped on the opponent's side; the solver considers
  both sides and uses whichever is better. The search models the value change on either
  table exactly, including the opponent's reaction.

## Build

```bash
./build.sh
```

This cross-builds `native/` for `x86_64-pc-windows-gnu` (rustup nightly, static CRT via
`native/.cargo/config.toml`) and then `dotnet build -c Release`, staging both
`bin/Release/OptimalPlay.dll` and `bin/Release/OptimalPlaySolver.dll`. The only system
dependency beyond rustup/dotnet is the mingw-w64 linker: `sudo pacman -S mingw-w64-gcc`.

`native/` is a normal cargo crate, so `cargo build --release` on the host builds a Linux
`.so` for local checks; the plugin uses the Windows DLL.

## Install

1. Install BepInEx 5 for Black Jacket.
2. Copy `OptimalPlay.dll` and `OptimalPlaySolver.dll` to `BepInEx/plugins/OptimalPlay/`.
3. Start a match; the draw phase is played automatically.

The plugin logs `Native solver loaded.` on startup. If the native library is missing or
was built for a different protocol version, it logs `Native solver unavailable (...)` and
leaves all input manual.

## Limitations

- The opponent's reshuffles (and yours) use a random order. The search stops at the
  shuffle and re-plans with the real order on the next turn.
- Card effects are modeled as far as `docs/effects.md` describes. Effects outside that
  list (shuffles, transforms, random choices, discard-pile picks, duo/trio events, ...)
  are skipped and each distinct one is logged once as `Unmodeled card effect: <name>`.
  The affected card is then scored as if the missing effect did nothing.
- Sleeving a card does not model triggers such as *"When you sleeve a card ..."* perks; the
  sleeve contents, their order and the sleeve costs are modeled exactly.
- Insight windows with more than four revealed cards only permute the top four; the rest
  keep their order to bound the search.
- Shop, map and reward screens stay manual.
- Auto-play is disabled during the tutorial.
