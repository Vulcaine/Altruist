# Altruist.Gaming.Rooms — player input

The room host applies one input per player per fixed step. Clients predict with the same input
stream, so the host's job is to apply exactly the client's inputs, in order, as soon as they arrive.
`InputBuffer<TInput>` does that for every participant automatically; the game only says what its
input is.

## Declaring the input (optional, recommended)

```csharp
public RoomInputModel<PadInput> InputModel { get; } = RoomInputModel<PadInput>.Describe()
    .Buttons(i => i.Buttons, (i, b) => i with { Buttons = b }) // held-buttons bitmask
    .Action(Jump, stage: 0)            // acts on its press; applied first in a step
    .Action(Fire, stage: 1)
    .Action(DashLeft, stage: 2)        // same stage: at most one of these per step,
    .Action(DashRight, stage: 2)       // declaration order breaks ties
    .Build();
```

`IRoomGame.InputModel` returns it (default `null`: inputs are opaque values). Everything else in the
input (axes, aim) is held state. A press is a rising edge of an action bit, so the stream itself says
which presses happened and in which order: presses in different steps in step order, presses in one
step in stage order (a client should send a press that would break that order one step later).

## What the buffer does

- **Sequencing**: stale and duplicate sequence numbers are dropped; `OfferRange(lastSeq, inputs)`
  accepts packets that repeat the last few inputs (redundancy) and keeps only the new ones.
- **Starvation**: a step with nothing queued repeats the held state (`WhenStarved` can clear one-shot
  fields); a held button stays held, so a press is never repeated.
- **Speculation**: that starved step stands in for the client's next input and is acked as such (up to
  `MaxSpeculation` in a row). When the late input arrives and equals what was applied it is dropped,
  so a late packet on steady input is invisible to the predicting client. When it differs it runs on
  a step of its own right away (late by one, every press kept, in order); the ack never goes back.
- **Standing queues** (a burst after a client hitch): a queue that stays above `MaxTarget` for
  `DrainWindow` steps is drained the same way, one merge per step (below that the client's pacing
  steers the depth; draining there would fight it).
- **Adaptive target**: `TargetDepth` (between `MinTarget` and `MaxTarget`, default 0–2) follows the
  measured arrival jitter. Send it to the client with the ack (`LastAppliedSeq`, `Depth`) so its input
  clock keeps that many inputs queued.
- **Overflow** (`MaxQueued`): merges first; only an input whose press can't be merged is dropped.
- **Accounting**: `Stats` (and the process-wide `InputBufferTotals`) count inputs offered, applied,
  starved, merged, dropped and presses offered / applied / lost / reordered / extra.

Tuning: `RoomHostOptions.Input = new InputBufferOptions { ... }` (`CatchUp = false` gives a plain
FIFO, one input per step).
