# InferHub v3.106.0 — a long context kept on the card, in RAM and on disk, by use (phase 153)

A model with a big context can hold a book, a codebase or months of notes as working memory. Reading that much takes time: ornith
reads 34k tokens in 36 s and 620k in 34 minutes. Once another conversation takes the engine's slot, the next question about the book
pays that again. 3.106 keeps a long context the way a CPU keeps memory, in three tiers:
- **slot:** the engine's own KV;
- **RAM:** llama.cpp's prompt cache;
- **disk:** slot files the node writes, restores and deletes by use.

## What is new

- **`Backend:Engines:{name}:Serve:Context`** on a llama.cpp engine with one `Serve:Model`. The keys are `Enabled`, `Directory`,
  `DiskMaxGiB` (64), `RamMiB` (8192), `MinPrefixTokens` (8192) and `SaveEveryTokens` (8192). The node launches `llama-server`
  with `-np 1 --slot-save-path <dir> --cache-ram <RamMiB>` and refuses those flags in `Serve:Arguments`. The context size, KV type,
  YaRN and `--n-cpu-moe` stay the operator's.
- **A file holds a clean prefix.** Before a turn that grew by `SaveEveryTokens`, the node renders the chat twice through the
  engine's own template: as sent, and with the last user message replaced. It then:
  1. takes the tokens the two renderings share;
  2. has the engine compute exactly those (`n_predict: 0`);
  3. writes the slot;
  4. sends the turn, which only extends it.

  A slot saved as a request left it would hold the answer's tokens. A hybrid model such as ornith (three of four layers
  recurrent) cannot roll those back, so it would compute everything again. Measured: a 0.35 s restore followed by 35 s of
  prompt.
- **A prefix comes back from where it is.** If the slot or llama.cpp's RAM cache holds the conversation (by the node's own
  reckoning of that LRU cache), the request goes straight through. Otherwise the longest matching file on disk is restored first.
  A new engine process resets the reckoning.
- **Kept by use.** When the disk budget is full, the file restored least often and least recently goes first. A shorter file of the
  same conversation that was never read back is replaced by the longer one. `POST /api/control/context/{engine}/{id}/pin`
  keeps a file forever, and `DELETE` removes one. The index lives beside the files and never holds text. Files are the truth: an
  entry without its file is dropped, and a file no entry names is deleted on load.
- **Visible:** `GET /api/control/context` on the node. On the coordinator, `engines[].context` on `/api/status` and a line in the
  console's Engines panel: how many requests came from the slot, RAM and disk and how many were computed, plus the files and
  sizes. Counts and sizes only.

## What does not change

`Serve:Context` off, which is the default, means no new flags, no tiers and no `context` field. A router engine, Ollama,
`generate`, the native routes and chats with pictures are not tiered. No new package.

## Verified

- `ContextTierTests` (10, Node). A llama-server stand-in over a socket checks:
  - the clean prefix is written before the answer, and only that prefix;
  - the slot, RAM and disk tiers, including disk after a restart;
  - a restarted engine;
  - a full disk with a pinned file;
  - a chat with a picture goes through untouched;
  - the index sweep, the launch flags and the refused configurations.
- Measured on llama-server directly (CUDA b11417, the official release zip), with ornith from its Ollama blob on the RTX 3090 Ti:
  - **34k tokens:** computed 36 s; from the slot 0.06 s; from RAM 0.06 s; from disk 0.35 s, plus 0.13 s for a question that
    extends a clean prefix.
  - **620k tokens** (War and Peace and part of Anna Karenina, YaRN ×4, `--n-cpu-moe 20`, q8_0 KV, 21.2 GB VRAM): prefill 34 min,
    save 10.4 s (6.8 GB), restore 5.5 s, then 0.7 s to the first token.
  - **Quality at 620k:** three facts hidden at 10 %, 50 % and 90 % depth came back exactly. A question about the epilogue's plot
    was answered wrong.
- Through a solo node built from source, with ornith as its one engine and two documents (README, 34k tokens; the coordinator's
  CLAUDE.md, 20k):

  | | time | served from |
  |---|---|---|
  | A, first question | 37.6 s | computed, then written |
  | A, follow-up | 1.6 s | slot |
  | B, first question | 22.3 s | computed, then written |
  | A, new question | 2.5 s | disk |
  | A and B after the node process restarted | 3.1 s / 2.2 s | disk |
  | A and B after only the engine restarted | 3.0 s / 2.1 s | disk |

## The suite

The whole solution on this machine: Shared 457, Coordinator 1090 (50 skipped), Node 797 (17 skipped), Mesh 559 (3 skipped), Desktop 36.

## Not established

- 600k tokens through the node. It was measured on llama-server directly; the node does the same calls.
- A RAM (L2) hit on live traffic. The node matches a RAM entry only as a whole prefix, and every live switch went to disk. The
  tests cover the RAM path.
- A coordinator routing a conversation back to the node that holds its files: conversation affinity does that, and it was not
  measured here.
- A Linux node and the published image. The phase changes the node; the published-image check below says what was run.
- Disk use under a long chat: each growth of `SaveEveryTokens` writes a new file (6.8 GB at 620k), and the budget evicts by use.
  A delta format does not exist in llama.cpp.
