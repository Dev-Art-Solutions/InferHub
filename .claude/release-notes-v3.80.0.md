# InferHub v3.80.0 — browse models that fit

Phase 119, plus InferHub Desktop 0.5.0 on its own tag. v3.79 answered "will *this* model fit?", but you had to arrive
with a link, found in a browser tab sorted for somebody else's machine. Today the top of Hugging Face's trending list
is a 200 GB DeepSeek, which on a 24 GB card is an hour-long disappointment. v3.80 lists what is trending and popular on
Hugging Face and in Ollama's library, with what fits this machine first. Each model opens Check fit, where you choose
the context and install it.

```http
GET /api/control/fit/catalog?source=all&sort=trending&kind=text&q=qwen&limit=30&fitsOnly=false
```

## What it does

- **Two sources.** Hugging Face's GGUF repositories (text, or `vision` for models that also read pictures), sorted by
  trending, downloads or newest. Ollama's library, sorted by popular or newest; it has no trending order, and the
  answer says so.
- **Ordered by where the weights alone would sit:** on the card, card + RAM (a split, or a mixture-of-experts with
  its experts in RAM), RAM on a machine without a card, size unknown, too big. Within a class, each source's own
  order. The machine is the same one Check fit sees: the same usable VRAM and RAM, lowered by your limits.
- **No context is claimed.** The list counts weights only and every line says so. `qwen2.5:32b` is "on the card"
  here at 18 GiB; Check fit at 32K says it splits, because Ollama's four parallel slots each hold the whole context.
  That is the intended division: the list finds candidates, Check fit does the arithmetic for your context.
- **A Hugging Face repo is sized by its files, not by its summary.** The node reads each repo's file list (one small
  API call, six at a time, kept ten minutes) and sizes every quant by its real bytes, grouped the way a pull groups
  them. An Ollama model is sized by its tags (`7b`, `32b`, `8x7b`) at 4-bit. Each entry opens at the largest size of
  its best class (a 4-bit K-quant when there is one); the other sizes are listed with their own class.
- **Consent per source**, as for Check fit: Hugging Face only with `HuggingFace:Enabled`, Ollama's library only on a
  node with an Ollama engine. A source that is off or fails says why in `sources`, and the other is still listed.
- **Nothing downloads from the list.** Ollama's library has no API, so the node reads its web page by hand. If the
  page changes shape, the answer says so instead of returning an empty list.

## Found live, fixed before release

Hugging Face's own summary of a repo is not the model. Each of these listed a ~200 GB model as "on the card" in the
first live run:

- **AtomicChat's DeepSeek-V4.1-Flash** had `gguf.total` = 115 M parameters: its LoRA's. Hence the file list.
- In the file list, **a LoRA, an importance matrix (`imatrix`) and a speculative-decoding draft (`mtp-…Q8_0.gguf`,
  `…-MTP-draft.gguf`)** each looked like a small quant of the model. They are no longer counted. A quant that carries
  its MTP layers (`IQ4_XS-MTP-00001-of-00003`) is the whole model, and stays.

## InferHub Desktop 0.5.0

**Browse models…** beside Check fit on the Models tab: source, sort, kind, a search and *fits only*, then the list as
the node orders it. Each row has the class and weights, the source's numbers, the node's reasons, a box of sizes, and
**Check fit…**, which opens 118's dialog on the chosen size. A source that did not answer shows as a red line; the other
is still listed. Needs a 3.80 node.

## Upgrading

No new configuration key. Nothing reaches the network until `/fit/catalog` is called; a node that never calls it
behaves as v3.79.

## Not established

- **Ollama's next redesign.** The library is read from HTML; the parser fails loudly, not silently, when it changes.
- **A side file named like none of the four patterns** (`lora`, `imatrix`, `mtp…`, `draft`) would still be listed
  as a quant.
- **Repos that name quants in ways 118's grouping does not.** They show by file name, as they do in Check fit.
- **A trending order for Ollama.** It has none.
- **`safetensors` checkpoints for colibri** are not listed: most are dense models no engine here loads. A pasted link
  still reaches Check fit.
- **The hub cannot browse for a node yet.** That is phase 120.

## Verification

`dotnet test InferHub.sln`: **2 564 passed, 63 skipped** (Desktop 36, Shared 433, Coordinator 896, Node 675, Mesh
524). The new tests parse a trimmed copy of the real ollama.com library page and a real Hugging Face listing, answer
the node's HTTP calls with the trap found live (a LoRA, an imatrix and two drafts beside a 200 GB model), and check
consent, per-source failure and the ten-minute cache.

Live on the 3090 Ti box (256 GB), a source build with Ollama and a Vulkan llama.cpp: both sources answered (cold, ~1.6 s
for 25 Hugging Face repos with their file lists). Trending text, top 30: Ollama's 8B–35B sizes and Hugging Face's
4B–33B repos on the card; the 122B and 200B mixture-of-experts models card + RAM; a 263 GB IQ1_S too big. One entry
(`qwen2.5:32b`) was opened in `/fit` at 32K. The desktop dialog was rendered from the app attached to that node.

## The published release, checked

`inferhub-node:3.80.0` was pulled from GHCR (revision label `75a1037`, the tag's commit). Its `app/` was extracted and
run natively on the same Windows box with Ollama and a Vulkan llama.cpp. These were the published binaries, not a
container, so the image's OS layer was not tested.

| Call | Result |
|---|---|
| `status` | `3.80.0` |
| `/fit/catalog?source=all&limit=10` | both sources answered; 17 on the card, 3 card + RAM, in that order |
| `?source=ollama&kind=vision&fitsOnly=true` | gemma3:27b, gemma4:31b, qwen3.5:35b, … — all on the card |
| `?source=huggingface&q=DeepSeek-V4.1-Flash-1017-Uncensored` | AtomicChat's repo is card + RAM at 233 GiB (IQ1_M), not its LoRA's 67 MiB |
| no token / `sort=best` | 401 / 400 naming the three sorts |

InferHub Desktop 0.5.0 (`InferHub-Desktop-0.5.0-windows-x64.zip`) was downloaded from its release. Its checksum
matches `SHA256SUMS.txt`, its Core is 0.5.0 and its node 3.80.0, both built from `75a1037`. Its packaged node was run
and answered the same catalogue. The packaged app's window was not opened; the dialog was rendered from the source
build of the same commit (`.claude/verification-v3.80.0-desktop.png`).
