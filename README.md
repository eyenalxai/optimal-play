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

Because card order is known, the bot can do things a human cannot: it knows when the
opponent is about to bust, when a card is safe to sleeve, and when standing pat already
wins the round. It also understands blackjack (two-card 21), busts, the single-card
tiebreak, the `Uprising` target reduction and the `Supper` pass restriction.

The game is re-read and re-solved on every one of your turns, so card effects that change
values on the fly are picked up as soon as they happen.

## Configuration

`BepInEx/config/com.blackjacket.mods.optimalplay.cfg`

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Enable the mod. |
| `AutoPlay` | `true` | Play automatically; toggle at runtime with `ToggleKey`. |
| `ToggleKey` | `F8` | Toggles AutoPlay. |
| `ActionDelay` | `0.5` | Seconds between actions, so animations/effects can finish. |
| `AutoActivateOptionalEffects` | `true` | Answer optional card-effect prompts with Activate (else Skip). |
| `LogDecisions` | `true` | Write every decision with its evaluation to the log. |
| `ShowStatus` | `true` | Small status line showing the current decision. |
| `SearchNodeBudget` | `250000` | Maximum search nodes per decision. |
| `SearchTimeMs` | `200` | Maximum search time per decision. |

## Install

1. Install BepInEx 5 for Black Jacket.
2. Copy `OptimalPlay.dll` to `BepInEx/plugins/OptimalPlay/`.
3. Start a match; the draw phase is played automatically.

## Limitations

- The opponent's reshuffles (and yours) use a random order. The search stops at the
  shuffle and re-plans with the real order on the next turn.
- Card effects are only modeled through the values they have already applied; anything a
  played card does that is not reflected in the current values is re-evaluated on the
  next turn.
- Choice dialogs (demands, card selections) are not answered; the bot waits while one is
  open.
- Shop, map and reward screens stay manual.
- Auto-play is disabled during the tutorial.
