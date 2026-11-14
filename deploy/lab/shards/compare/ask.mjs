// Phase 141: one streamed request, timed from the client — the same clock for every engine, so no engine's own
// idea of "first token" decides the comparison. Prints one JSON line.
//
//   node ask.mjs --url http://127.0.0.1:18080 --mode completion --prompt-file prompts/short.txt --n 48
//   node ask.mjs --url http://127.0.0.1:15080 --mode chat --model llama-8b-split --bearer cli135 --prompt-file … --n 48
//
// completion = llama-server's native /completion (the raw prompt, no chat template: identical input for every engine
// that descends from llama.cpp). chat = /v1/chat/completions, the only surface an InferHub hub offers.
// --abort-after N closes the connection after N content chunks (the cancellation check).
import { readFileSync } from 'node:fs';

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, arg, i, all) =>
  arg.startsWith('--') ? [...pairs, [arg.slice(2), all[i + 1]]] : pairs, []));
// --prefix puts a run's own words first: a hub has no cache_prompt switch, and a server that kept an earlier run's prompt
// would answer the first token of a long one in milliseconds.
const prompt = (args.prefix ?? '') + (args['prompt-file'] ? readFileSync(args['prompt-file'], 'utf8') : args.prompt);
const n = Number(args.n ?? 48);
const abortAfter = args['abort-after'] ? Number(args['abort-after']) : 0;
const chat = args.mode === 'chat';
const headers = { 'Content-Type': 'application/json' };
if (args.bearer) headers.Authorization = `Bearer ${args.bearer}`;
if (args.header) { const [name, ...rest] = args.header.split(':'); headers[name.trim()] = rest.join(':').trim(); }

const body = chat
  ? { model: args.model, messages: [{ role: 'user', content: prompt }], max_tokens: n, temperature: 0, seed: 1, stream: true }
  : { prompt, n_predict: n, temperature: 0, seed: 1, cache_prompt: false, stream: true };

const out = { ok: false, mode: chat ? 'chat' : 'completion', chunks: 0, text: '' };
const controller = new AbortController();
const timer = setTimeout(() => controller.abort(), Number(args.timeout ?? 600) * 1000);
const started = performance.now();
let first = 0, last = 0;
try {
  const response = await fetch(args.url + (chat ? '/v1/chat/completions' : '/completion'),
    { method: 'POST', headers, body: JSON.stringify(body), signal: controller.signal });
  out.status = response.status;
  if (!response.ok) { out.error = (await response.text()).slice(0, 400); throw new Error('refused'); }
  const decoder = new TextDecoder();
  let pending = '';
  read: for await (const bytes of response.body) {
    pending += decoder.decode(bytes, { stream: true });
    let end;
    while ((end = pending.indexOf('\n')) >= 0) {
      const line = pending.slice(0, end).trim();
      pending = pending.slice(end + 1);
      if (!line.startsWith('data:')) continue;
      const data = line.slice(5).trim();
      if (data === '[DONE]') break read;
      const event = JSON.parse(data);
      const piece = chat ? event.choices?.[0]?.delta?.content : event.content;
      if (piece) {
        const now = performance.now();
        if (!first) first = now;
        last = now;
        out.chunks++;
        out.text += piece;
        if (abortAfter && out.chunks >= abortAfter) { out.aborted = true; controller.abort(); break read; }
      }
      if (event.timings) out.timings = event.timings;
      if (event.usage) out.usage = event.usage;
    }
  }
  out.ok = out.chunks > 0;
} catch (error) {
  if (!out.aborted) out.error ??= String(error.message ?? error);
  else out.ok = true;
}
clearTimeout(timer);
out.total_ms = Math.round(performance.now() - started);
if (first) {
  out.ttft_ms = Math.round(first - started);
  // Decode speed from the first token on, so a long prompt's prefill is not charged to generation.
  if (out.chunks > 1) out.decode_tps = Number(((out.chunks - 1) / ((last - first) / 1000)).toFixed(3));
}
if (out.timings) {
  out.engine_prompt_tps = out.timings.prompt_per_second;
  out.engine_prompt_n = out.timings.prompt_n;
  out.engine_decode_tps = out.timings.predicted_per_second;
}
delete out.timings;
console.log(JSON.stringify(out));
