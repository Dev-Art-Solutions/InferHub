// Phase 147: K requests sent at the same instant, timed from one client. Prints one JSON line: the wall time from the first
// send to the last token, the tokens all of them produced, and each request's own speed and text.
//
//   node burst.mjs --url http://127.0.0.1:18080 --k 4 --n 64 --prompt-file prompts/short.txt
//   node burst.mjs --url http://127.0.0.1:15080 --chat --model llama-8b-split --bearer cli135 --k 4 --n 64 --prompt-file …
//
// Every request carries the same prompt at temperature 0, so every text must be the one a single request gets — a batch
// that changes the answer is a finding, not noise. Completion mode sets cache_prompt false; chat mode cannot (a hub has no
// such switch), and the prompt is the same short one each time, so a reused prefix is part of what a client would see.
import { readFileSync } from 'node:fs';

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, arg, i, all) =>
  arg.startsWith('--') ? [...pairs, [arg.slice(2), all[i + 1]?.startsWith('--') ? 'true' : all[i + 1]]] : pairs, []));
const prompt = args['prompt-file'] ? readFileSync(args['prompt-file'], 'utf8') : args.prompt;
const n = Number(args.n ?? 64);
const k = Number(args.k ?? 1);
const chat = args.chat === 'true';
const headers = { 'Content-Type': 'application/json' };
if (args.bearer) headers.Authorization = `Bearer ${args.bearer}`;

async function one(i, started) {
  const body = chat
    ? { model: args.model, messages: [{ role: 'user', content: prompt }], max_tokens: n, temperature: 0, seed: 1, stream: true }
    : { prompt, n_predict: n, temperature: 0, seed: 1, cache_prompt: false, stream: true };
  const out = { i, chunks: 0, text: '' };
  let first = 0, last = 0;
  try {
    const response = await fetch(args.url + (chat ? '/v1/chat/completions' : '/completion'),
      { method: 'POST', headers, body: JSON.stringify(body), signal: AbortSignal.timeout(Number(args.timeout ?? 900) * 1000) });
    out.status = response.status;
    if (!response.ok) { out.error = (await response.text()).slice(0, 300); return out; }
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
        }
      }
    }
  } catch (error) {
    out.error = String(error.message ?? error);
  }
  out.ttft_ms = first ? Math.round(first - started) : null;
  out.end_ms = Math.round((last || performance.now()) - started);
  if (out.chunks > 1) out.decode_tps = Number(((out.chunks - 1) / ((last - first) / 1000)).toFixed(3));
  return out;
}

const started = performance.now();
const each = await Promise.all(Array.from({ length: k }, (_, i) => one(i, started)));
const wall = Math.max(...each.map(r => r.end_ms));
const tokens = each.reduce((sum, r) => sum + r.chunks, 0);
const texts = [...new Set(each.map(r => r.text))];
console.log(JSON.stringify({
  k, n, ok: each.every(r => !r.error && r.chunks > 0), wall_ms: wall, tokens,
  aggregate_tps: Number((tokens / (wall / 1000)).toFixed(3)),
  ttft_ms: each.map(r => r.ttft_ms), end_ms: each.map(r => r.end_ms), decode_tps: each.map(r => r.decode_tps ?? null),
  same_text: texts.length === 1, texts, errors: each.filter(r => r.error).map(r => `${r.status ?? ''} ${r.error}`)
}));
