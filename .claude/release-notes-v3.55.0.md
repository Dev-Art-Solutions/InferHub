# InferHub v3.55.0 — a Bulgarian voice that runs on a CPU, fetched by name

Until now InferHub had one Bulgarian voice: `bg-tts-v5` (v3.49). It is a 250M-parameter model with
its own ~9 GB image (`:tts-bg`), and it needs a GPU to be usable. That is a lot to ask of somebody
who wants a Bulgarian answer read aloud on a small box.

The Piper project has since published **`bg_BG-dimitar-medium`**, a 63 MB Bulgarian voice (MIT,
trained on CC0 data). It runs in the `:tools` image you may already have, on the CPU. On the machine
this was built on it read a 5.2-second sentence in 1.5 seconds.

Piper could already speak with any voice you put on the box. Getting the voice there was the hard
part: you had to `docker exec` into the container, `curl` two files from a moving branch, and
restart. v3.55 replaces that with one setting.

## What's new

```
Tools__Speech__Voices__0=bg_BG-dimitar-medium
```

- **Name a voice and the node fetches it.** The id is looked up in a small catalogue that ships in
  the image (`python/voices/`, one file per voice). Each entry pins a commit of
  `rhasspy/piper-voices` plus the size and sha256 of the voice's two files. The download runs in the
  background, and the voice is offered as soon as it lands. There is no restart, and no request
  waits for it.
- **Nothing is installed until it matches.** Both files are downloaded to `.part` files and checked
  against their pinned size and hash. Only then are they renamed into `/data/tools/voices`, with the
  config file last. A file that does not match is logged with both hashes, and neither file is
  installed, not even the one that was right.
- **Nothing is fetched unless you name it.** The catalogue ships entries, not a default voice. A
  default voice would be a default language, and a confident answer in the wrong one is worse than
  none. That was true in v3.10 and it is still true.
- **`Tools:AllowModelDownload` still has the last word.** With it off, the worker fetches nothing
  and logs the two exact URLs to download by hand.
- **Two voices in the catalogue:** `bg_BG-dimitar-medium` (Bulgarian) and `en_US-amy-medium` (the
  voice every example in the docs already uses). Any other Piper voice still works the old way:
  drop its `.onnx` + `.onnx.json` pair into `/data/tools/voices` and restart.
- **A voice id is a file name, so it is checked.** Anything other than letters, digits, `_` and `-`
  stops the node at startup, naming the key. The worker checks it again.
- **A mirror works.** `HF_ENDPOINT` in the piper manifest's `env` points the fetch somewhere other
  than huggingface.co.
- `compose.tools.yml` no longer has a manual step. It names `en_US-amy-medium`, and the Bulgarian
  voice is one commented line below it.

`bg-tts-v5` is unchanged. It still sounds better and has two speakers. The difference now is that
it is the choice for a box with a card, not the only Bulgarian voice there is.

## How it is tested

- `VoiceCatalogueTests` (Mesh) run the shipped `piper_worker.py`, with a local HTTP server standing
  in for Hugging Face. They cover: every shipped entry is pinned and names its own pair; a matching
  pair lands with no `.part` left behind; a mismatched config keeps **both** files out; the plan
  refuses an unknown id, a path-shaped id and a download that is not allowed, each with its reason;
  and the real worker process declares `fetching` at handshake and then declares the voice when it
  lands.
- `ToolSecurityTests`: the list reaches the worker trimmed and without duplicates, and it is empty
  (not missing) by default. `../etc/passwd`, `voices/bg`, a file name and a Windows path each fail
  startup naming the key.
- Mutation checks: renaming before verifying turns the mismatch test red, and not declaring
  `fetching` turns the protocol test red.
- The real voice, before the image: fetched through the worker's own `fetch_voice` from the pinned
  commit (63 MB in 3.4 s), both hashes matched, and synthesised under the pinned
  `piper-tts==1.6.0`: 5.2 s of 22.05 kHz audio in 1.54 s, real signal, phonemised by espeak-ng as
  Bulgarian (`z d r a v ˈe j t e` for «Здравейте»).
- `dotnet test InferHub.sln`: **1728 passed, 53 skipped** (Shared 202, Coordinator 820, Node 230,
  Mesh 476).

Zero new `PackageReference`, `InferHub.Shared.csproj` is still empty, and no new pip requirement:
the fetch is Python's standard library.

## Checked on the published image

*Pending: this section is filled in once the tag's images are published and pulled.*

## Not established, said out loud

- **How the voice sounds to a Bulgarian listener.** It is a medium-quality, single-speaker Piper
  voice. The checks above show real Bulgarian phonemes and real audio, not that it reads naturally.
- **A fetch through a real mirror.** `HF_ENDPOINT` is exercised against a local server in the
  tests, not against a production mirror.
