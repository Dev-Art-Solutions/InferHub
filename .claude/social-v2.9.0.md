# InferHub v2.9.0 — social copy

**Status: written, NOT posted.** No FB/X connector — the standing debt across every release since v2.4.
r/selfhosted and r/LocalLLaMA drafts included; post by hand.

---

## Facebook

> InferHub 2.9 finishes the job on tool calling.
>
> Function calls now stream as proper `delta.tool_calls` frames on the OpenAI-compatible `/v1`
> surface — so your streaming agent loops (OpenAI SDK, LangChain, LlamaIndex) get the call the
> moment it lands, not just a `finish_reason` with nothing attached.
>
> The honest bit: Ollama emits its tool call in the final message chunk, not incrementally, so
> the call arrives as one delta on the last frame — we don't fake OpenAI-style argument
> fragmentation that never happened. Text streaming is byte-identical to before.
>
> Self-hosted, MIT, .NET 10. Still zero new dependencies.
> 👉 https://github.com/Dev-Art-Solutions/InferHub

## X

> InferHub 2.9: streaming `tool_calls` on the OpenAI `/v1` surface. Your streaming agent loop now
> gets the function call as it lands, not just a finish_reason. Node protocol unchanged, zero new
> deps. MIT, .NET 10.
> https://github.com/Dev-Art-Solutions/InferHub

_(≈240 chars incl. the t.co-counted link — under 280.)_

## r/selfhosted / r/LocalLLaMA (draft)

**Title:** InferHub 2.9: streaming tool_calls on the OpenAI-compatible surface of a self-hosted mesh

Body: InferHub is a self-hosted, Ollama/OpenAI-compatible inference mesh in .NET — a coordinator
on an always-on box, nodes on your GPU machines that dial out (no port forwarding). 2.9 closes a
gap in the OpenAI surface: tool calls were mapped in blocking mode only, so a *streaming* agent
loop got `finish_reason=tool_calls` with no call attached. Now function calls stream as real
`delta.tool_calls` frames the OpenAI SDK parses into a `ChoiceDeltaToolCall`. Because Ollama emits
the call in its final chunk rather than incrementally, the call lands as one delta on the last
frame — we deliberately don't fabricate argument-fragment streaming Ollama never produced.
Text-only streaming is byte-identical to before. MIT, zero new deps.
