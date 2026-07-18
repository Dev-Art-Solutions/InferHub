# InferHub v2.9.0 — Streaming `tool_calls` deltas

A half-finished feature, finished. Tool calls were mapped in **blocking** mode only: on the
streaming `/v1/chat/completions` path the delta carried `role` and `content` and nothing else,
so the terminal chunk resolved `finish_reason=tool_calls` with no call attached. A streaming
agent loop (OpenAI SDK function-calling, LangChain, LlamaIndex) got a finish reason it could
not act on. Now the call streams too.

## What changed

- The streaming delta grows a `tool_calls` slot (`ChatCompletionDelta.ToolCalls`), and
  `ResponseTranslator.ToChatChunk` fills it when the Ollama chunk carries
  `message.tool_calls`. Each entry gets the 0-based `index` the streaming spec requires, a
  synthesized `id`, an explicit `type: function`, and arguments serialized to a **string** —
  the same shape the OpenAI Python SDK parses into a `ChoiceDeltaToolCall`.
- A tool-call delta carries `content: null`, not `""` — a tool-call frame and a text frame
  are not the same frame, matching OpenAI.
- The streamed call reuses the blocking extractor (`ExtractToolCalls`), so the function name
  and stringified arguments are **byte-equivalent** (synthesized id aside) to what blocking
  `ToChatCompletion` produces for the same Ollama body. `OpenAiTranslationTests` pins the parity.

## The honest part

The whole tool call arrives in **one** delta — we deliberately do **not** fabricate
OpenAI-style argument-fragment streaming that Ollama never sent. It is marked in a comment so
nobody "improves" it into fake fragmentation; if a future Ollama actually streams partial
arguments, that is a separate change.

## Two things the tests didn't catch — the live node did

The phase brief assumed Ollama emits `message.tool_calls` on its **final** chunk (`done:true`).
Verified against a real `qwen2.5` node, it does **not**: the call lands on a **non-terminal**
chunk (`done:false`), and the terminal chunk carries no tool_calls. A stateless per-chunk
translator would emit `finish_reason: stop` on that terminal frame — and a strict agent loop
keys tool execution off `finish_reason == "tool_calls"`, so the call would stream but never
fire. The stream formatter now **remembers** that a call was seen and resolves the terminal
frame to `tool_calls` regardless of which chunk carried it.

The second half of the loop was also broken, in the **request** translator (untouched since
phase 21): OpenAI clients serialize `function.arguments` as a JSON **string** (as does our new
streamed delta), but Ollama emits and expects an **object**. A prior assistant turn's tool call
therefore reached the model as a string it could not read, and the model answered **empty** —
so the streamed call → tool result → answer loop never closed. The translator now parses the
arguments string back to an object. Both fixes were found by running the real loop, not by a
green suite — the unit tests passed happily while the live round-trip returned nothing.

## Not a regression

Text-only streaming is **byte-identical** to v2.8 — ordinary deltas carry no
`tool_calls: null` field (the slot is omitted when null). The node-facing job protocol is
unchanged; the node keeps sending Ollama-shaped chunks.

## Tests

527 total (510 passed, 17 skipped — the gated Postgres integration tests, as always). New
coverage: `OpenAiStreamingTests` (a terminal `tool_calls` chunk yields a `delta.tool_calls`
frame with a synthesized id, `index: 0`, `function.name`, stringified `arguments` and
`finish_reason: tool_calls`; a call on a **non-terminal** chunk still resolves the terminal
frame to `tool_calls`, not `stop`; ordinary text deltas carry no `tool_calls` field), and
`OpenAiTranslationTests` (the streamed call is byte-equivalent to the blocking one, id aside; a
text delta serializes without a `tool_calls` key; a prior tool call's string arguments are
parsed back to an object, and an object passes through unchanged).

## Verified live

A real coordinator + `qwen2.5` Ollama node: `stream:true` with a `get_weather` tool → a
`delta.tool_calls` frame then `finish_reason: tool_calls`, and a second turn carrying the tool
result → a grounded streamed answer. The full call → tool-result → answer loop closes with the
arguments in the spec's string form.

## Zero new dependencies

Rule 5 holds: `System.Text.Json` did all of it, and the SSE framing is still hand-written.
