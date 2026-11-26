# InferHub v3.116.0: a burst waits its turn

Phase 155. The slot wait from v3.74 (`Queue:SlotWaitSeconds`) held a request only when it found every slot taken on
arrival. Requests that arrived together all looked before the first one was counted, so all of them found a free slot
and all of them went. Four chats at once to a node with two slots: the hub held none, and two waited inside the engine.
v3.112 measured it through a split model (`waited 0`, first tokens at 11.9 s) and recorded it as found, not changed.

## What changed

- **A request that finds a free slot takes the slot wait's line too.** It looks, it is routed and sent, and the next one
  looks only after this one is counted against its node. That is v3.74's own rule for waiters, now applied to arrivals.
- **Only where it can matter.** `Queue:SlotWaitSeconds` set, and some node holding the model declared a slot count.
  A model whose nodes declared none never waits and never takes the line.
- **`queue.slotWait.waited` counts the requests the hub held.** A request that stood behind another's dispatch for
  microseconds is not counted. One that stood behind the line's head while every slot was taken is.
- Essential work (v3.86) still skips the wait. The cap queue (`MaxConcurrency`, v2.7) is unchanged.

## Measured

A coordinator and a node built from source. `Node:ModelSlots=2`, `Queue:SlotWaitSeconds=20`. Behind the node, a stand-in
OpenAI-shaped engine that answers in 3 s and counts how many requests it is running at once.

| Chats sent together | v3.115 (HEAD): at once in the engine | v3.116: at once | v3.116 `slotWait` |
|---|---|---|---|
| 4 | 4 (`waited 0`) | 2 | `waited 2`, `admitted 2`, `sentAnyway 0` |
| 8 | — | 2 | `waited 6`, `admitted 6`, `sentAnyway 0` |

With four, the two the hub held got their first piece at 3.3 s instead of sharing the engine from the start. Every
answer had the same text.

## Tests

- `SlotWaitTests.FourChatsAtOnceOverTwoSlotsSendTwoAndHoldTwo`: the registry's first four reads wait for each other, so
  all four requests look at once on every run. It fails on 3.115 (four sent) and passes on 3.116, three runs each.
- `WhereNobodyCountsSlotsNoRequestEvenPassesTheTurnstile`, and `ANodeThatDeclaredNothingIsAlwaysFreeSoNobodyWaits`
  now checks the request passes without counting as a wait.
- `ThirdChatWaitsAtTheHubAndGoesToTheNodeThatFreesFirst` no longer assumes which of two idle nodes the first chat
  lands on: the router now routes again under the ticket.
- The whole solution: 3067 passed, 56 skipped (Postgres, Qdrant and other gated suites), 0 failed.

## Not established

- **A real engine.** The stand-in only counts; it says nothing about how fast llama.cpp or Ollama serves two instead of
  four. v3.112's figures for a split model stand as they were.
- **Several nodes, and a cluster.** The line is per model on one coordinator. Two coordinators of a cluster (v3.92)
  each keep their own.
- **The published image** is checked by its revision label and by the coordinator's DLL, not by a run of this lab on it.

Zero new `PackageReference`; `InferHub.Shared.csproj` is still empty; nothing is persisted.
