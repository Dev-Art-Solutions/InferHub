// Phase 141: reads what compare-engines.sh measured, writes summary.{md,json}, prints the gates and exits 1 when a
// required one failed. A number that was not measured is written "not established", never filled in.
//
//   node report.mjs <results directory>
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';

const dir = process.argv[2];
const lines = name => existsSync(join(dir, name))
  ? readFileSync(join(dir, name), 'utf8').split('\n').filter(Boolean).map(line => JSON.parse(line)) : [];
const meta = JSON.parse(readFileSync(join(dir, 'meta.json'), 'utf8'));
const results = lines('results.jsonl'), loads = lines('load.jsonl'), memory = lines('memory.jsonl');
const faults = lines('faults.jsonl'), skips = lines('skips.jsonl');

// Which whole-model run a split must answer like, and what each configuration is.
const WHOLE_OF = { 'rpc-star': 'whole', 'inferhub-group': 'whole-chat', 'prima-ring': 'prima-whole', 'rpc-draft': 'whole', 'inferhub-group-draft': 'whole-chat' };
// Phase 146: a draft model on the head, each against the same split without one (the same engine, the same whole model).
const DRAFT_OF = { 'rpc-draft': 'rpc-star', 'inferhub-group-draft': 'inferhub-group' };
const BASELINE = ['whole', 'whole-chat', 'rpc-star', 'inferhub-group'];
const CANDIDATE = 'prima-ring';
// Read from the candidate's source at the pinned commit (docs/research/distributed-inference-engines.md), not measured here.
const CANDIDATE_BLOCKERS = 'its ranks talk over plain ZeroMQ push/pull sockets with no authentication or encryption, '
  + 'it is a 2024 fork of llama.cpp (not the build a node runs) and it has no Windows build';

const median = values => { const s = [...values].sort((a, b) => a - b); return s.length ? (s[(s.length - 1) >> 1] + s[s.length >> 1]) / 2 : null; };
const stat = values => { const v = values.filter(x => typeof x === 'number'); return v.length ? { median: median(v), min: Math.min(...v), max: Math.max(...v), n: v.length } : null; };
const show = (s, digits = 2, unit = '') => s ? `${s.median.toFixed(digits)}${unit} (${s.min.toFixed(digits)}–${s.max.toFixed(digits)})` : 'not established';

const configs = [...new Set(results.map(r => r.config))];
const rows = [];
for (const config of configs) {
  for (const rtt of [...new Set(results.filter(r => r.config === config).map(r => r.rtt_ms))]) {
    const at = prompt => results.filter(r => r.config === config && r.rtt_ms === rtt && r.prompt === prompt && r.ok);
    const short = at('short'), long = at('long'), all = [...short, ...long];
    rows.push({
      config, rtt_ms: rtt, ping_ms: stat(all.map(r => r.ping_ms)),
      ttft_short_ms: stat(short.map(r => r.ttft_ms)), ttft_long_ms: stat(long.map(r => r.ttft_ms)),
      decode_tps: stat(all.map(r => r.decode_tps)),
      decode_short: stat(short.map(r => r.decode_tps)), decode_long: stat(long.map(r => r.decode_tps)),
      prefill_tps: stat(long.map(r => r.engine_prompt_tps)),
      bytes_per_token: stat(short.map(r => r.net_bytes / r.chunks)),
      cpus_busy: stat(all.map(r => r.cpu_usec / 1000 / r.total_ms)),
    });
  }
}
const row = (config, rtt) => rows.find(r => r.config === config && r.rtt_ms === rtt);
for (const r of rows) {
  const whole = row(WHOLE_OF[r.config], 0);
  r.of_own_whole = whole?.decode_tps && r.decode_tps ? r.decode_tps.median / whole.decode_tps.median : null;
}

// --- gates
const out = []; let failed = 0;
const gate = (ok, what) => { out.push(`  ${ok ? 'PASS' : 'FAIL'}  ${what}`); if (!ok) failed++; };
const text = (config, prompt) => [...new Set(results.filter(r => r.config === config && r.prompt === prompt).map(r => r.text))];
const ran = config => configs.includes(config);
const wanted = meta.only.split(' ');

const missing = wanted.filter(c => !ran(c) && !skips.some(s => s.config === c));
const baselineMissing = wanted.filter(c => BASELINE.includes(c) && !ran(c));
gate(missing.length === 0 && baselineMissing.length === 0,
  `baseline and candidate configurations recorded (${configs.join(', ') || 'none'}; skipped: ${skips.map(s => s.config).join(', ') || 'none'})`
  + (baselineMissing.length ? ` — the baseline did not run: ${baselineMissing.join(', ')}` : '') + (missing.length ? ` — neither run nor skipped: ${missing.join(', ')}` : ''));

const unequal = results.filter(r => !r.ok || r.chunks !== meta.tokens);
let same = unequal.length === 0 && Boolean(meta.model_sha256);
let sameNote = `same test prompts, model ${meta.model_sha256.slice(0, 12)}…, quantization and output settings (${results.length} runs of ${meta.tokens} tokens at temperature 0)`;
if (unequal.length) sameNote += ` — ${unequal.length} runs failed or stopped short: ${[...new Set(unequal.map(r => `${r.config}@${r.rtt_ms}`))].join(', ')}`;
if (meta.expect_revision && ran('inferhub-group')) {
  const built = meta.hub_revision.startsWith(meta.expect_revision) && meta.node_revision.startsWith(meta.expect_revision);
  same &&= built; sameNote += built ? `; images built from ${meta.expect_revision}` : ` — images are ${meta.hub_revision} / ${meta.node_revision}, not ${meta.expect_revision}`;
}
gate(same, sameNote);

const correct = config => {
  const problems = [];
  // Through /v1/chat/completions the long prompt carries each run's own first words (see compare-engines.sh): no text to compare.
  const chat = results.some(r => r.config === config && r.mode === 'chat');
  for (const prompt of chat ? ['short'] : ['short', 'long']) {
    const mine = text(config, prompt);
    if (mine.length !== 1) problems.push(`${prompt}: ${mine.length} different texts over the repetitions`);
    const whole = WHOLE_OF[config] && text(WHOLE_OF[config], prompt);
    if (whole && (whole.length !== 1 || whole[0] !== mine[0])) problems.push(`${prompt}: not the text of ${WHOLE_OF[config]}`);
  }
  return problems;
};
const baselineProblems = configs.filter(c => BASELINE.includes(c)).flatMap(c => correct(c).map(p => `${c} ${p}`));
const draftProblems = Object.keys(DRAFT_OF).filter(ran).flatMap(c => correct(c).map(p => `${c} ${p}`));
const candidateProblems = ran(CANDIDATE) ? [...correct('prima-whole').map(p => `prima-whole ${p}`), ...correct(CANDIDATE).map(p => `${CANDIDATE} ${p}`)] : null;
gate(baselineProblems.length === 0, 'correctness / compatibility thresholds held: every split answers its own whole model\'s text, every repetition the same'
  + (baselineProblems.length ? ` — ${baselineProblems.join('; ')}` : '')
  + (draftProblems.length ? ` · draft DISQUALIFIED: ${draftProblems.join('; ')}` : Object.keys(DRAFT_OF).some(ran) ? ' · the draft runs too' : '')
  + (candidateProblems === null ? '' : candidateProblems.length ? ` · candidate DISQUALIFIED: ${candidateProblems.join('; ')}` : ' · the candidate too'));

const unmeasured = [];
for (const config of configs) {
  const mine = rows.filter(r => r.config === config);
  if (mine.some(r => !r.ttft_short_ms || !r.ttft_long_ms || !r.decode_tps || !r.bytes_per_token || !r.cpus_busy)) unmeasured.push(`${config}: latency/throughput/traffic`);
  if (!memory.some(m => m.config === config && m.peak_bytes)) unmeasured.push(`${config}: memory`);
  if (!config.endsWith('-chat') && !loads.some(l => l.config === config)) unmeasured.push(`${config}: load time`);
}
gate(unmeasured.length === 0 && configs.length > 0, 'latency, throughput, memory and network results written to artifacts'
  + (unmeasured.length ? ` — missing: ${unmeasured.join('; ')}` : ''));

// --- the decision this run supports. A rule, so that two runs cannot be read two ways; the argument is in the decision document.
let decision;
// The baseline is the InferHub group when it ran (D2); the bare star only stands in for it.
const base = ran('inferhub-group') ? 'inferhub-group' : 'rpc-star';
const rtts = [...new Set(rows.filter(r => r.config === CANDIDATE).map(r => r.rtt_ms))].filter(rtt => row(base, rtt));
const share = (config, rtt, prompt) => row(config, rtt)[prompt].median / row(WHOLE_OF[config], 0)[prompt].median;
let edge = [];
if (!ran(CANDIDATE) || !ran(base)) decision = 'defer — no candidate was measured beside the baseline in this run';
else if (candidateProblems.length) decision = `reject — the candidate does not answer its own whole model's text (${candidateProblems[0]})`;
else {
  // Each engine against its own whole model, per prompt: the candidate must be ahead on every prompt, not on the median of both.
  edge = rtts.map(rtt => ({ rtt, short: share(CANDIDATE, rtt, 'decode_short') / share(base, rtt, 'decode_short'),
    long: share(CANDIDATE, rtt, 'decode_long') / share(base, rtt, 'decode_long') }));
  const said = edge.map(e => `${Math.min(e.short, e.long).toFixed(2)}–${Math.max(e.short, e.long).toFixed(2)}× at ${e.rtt} ms`).join(', ');
  const shaped = edge.filter(e => e.rtt > 0);
  const always = shaped.some(e => Math.min(e.short, e.long) >= 1.25), sometimes = shaped.some(e => Math.max(e.short, e.long) >= 1.25);
  decision = always
    ? `adapt — do not adopt Prima.cpp (${CANDIDATE_BLOCKERS}), but against ${base} its ring keeps ${said} of the share of whole-model speed on every prompt: one hop per boundary instead of a star is worth a bounded phase`
    : sometimes
      ? `defer — do not adopt Prima.cpp (${CANDIDATE_BLOCKERS}); against ${base} its ring keeps ${said} of the share of whole-model speed, past 1.25× on one prompt and not the other: a gain on a wide network only, and not yet one that pays for a second engine`
      : `reject — against ${base} the ring keeps ${said} of the share of whole-model speed, under the 1.25× that would pay for a second engine`;
}

// Phase 146's rule is 141's: the draft enters the product only if it keeps at least 1.25× the speed of the same split without
// it on EVERY prompt at some shaped round trip. Same engine and same whole model on both sides, so the ratio is direct.
const draftEdges = [];
const draftDecisions = Object.entries(DRAFT_OF).filter(([d, b]) => ran(d) && ran(b)).map(([d, b]) => {
  const at = [...new Set(rows.filter(r => r.config === d).map(r => r.rtt_ms))].filter(rtt => row(b, rtt));
  const e = at.map(rtt => ({ config: d, rtt, short: row(d, rtt).decode_short.median / row(b, rtt).decode_short.median,
    long: row(d, rtt).decode_long.median / row(b, rtt).decode_long.median }));
  draftEdges.push(...e);
  const said = e.map(x => `${x.short.toFixed(2)}× / ${x.long.toFixed(2)}× at ${x.rtt} ms`).join(', ');
  const problems = draftProblems.filter(p => p.startsWith(d + ' '));
  if (problems.length) return `${d}: reject — it does not answer its whole model's text (${problems[0]})`;
  const shaped = e.filter(x => x.rtt > 0);
  const always = shaped.some(x => Math.min(x.short, x.long) >= 1.25), sometimes = shaped.some(x => Math.max(x.short, x.long) >= 1.25);
  return `${d}: ${always ? 'adopt' : sometimes ? 'defer' : 'reject'} — against ${b} (short / long): ${said}`;
});

// --- summary
const md = [];
md.push(`# compare-engines — ${meta.date}`, '',
  `InferHub \`${meta.inferhub_commit.slice(0, 7)}\`, published images ${meta.inferhub_tag} (revision \`${meta.hub_revision.slice(0, 7)}\`), llama.cpp \`${meta.llama_commit.slice(0, 7)}\` (b12182), Prima.cpp \`${meta.prima_commit.slice(0, 7)}\` (layers ${meta.prima_layers}).`,
  `Model \`${meta.model}\` sha256 \`${meta.model_sha256}\` (${(meta.model_bytes / 2 ** 30).toFixed(2)} GiB). ${meta.cpus_per_container} CPUs per container, ${meta.worker_memory} per worker (${meta.node_memory} per InferHub node), host ${meta.host_cpus} CPUs. ${meta.reps} repetitions, ${meta.tokens} tokens, temperature 0. Values are median (min–max).`, '',
  '| Configuration | Round trip set / measured | First token, short prompt | First token, long prompt | Generate | short / long prompt | Of its own whole model | Prefill (engine\'s own count) | Bytes per token | CPUs busy |',
  '|---|---|---:|---:|---:|---:|---:|---:|---:|---:|');
for (const r of rows) {
  md.push(`| ${r.config} | ${r.rtt_ms} ms / ${r.ping_ms ? r.ping_ms.median.toFixed(2) + ' ms' : '—'} | ${show(r.ttft_short_ms, 0, ' ms')} | ${show(r.ttft_long_ms, 0, ' ms')} | ${show(r.decode_tps, 2, ' tok/s')} | ${r.decode_short?.median.toFixed(2) ?? '—'} / ${r.decode_long?.median.toFixed(2) ?? '—'} | ${r.of_own_whole ? (r.of_own_whole * 100).toFixed(0) + ' %' : '—'} | ${show(r.prefill_tps, 1, ' tok/s')} | ${show(r.bytes_per_token, 0)} | ${show(r.cpus_busy, 2)} |`);
}
md.push('', '## Load', '', '| Configuration | Until it answers | Bytes sent between containers by then | |', '|---|---:|---:|---|');
for (const l of loads) md.push(`| ${l.config} | ${l.load_s.toFixed(0)} s | ${(l.load_tx_bytes / 2 ** 20).toFixed(0)} MiB | ${l.note} |`);
md.push('', '## Memory (after the runs; the peak counts page cache, which a container under its limit gives back)', '',
  '| Configuration | Container | Peak | Anonymous | File cache |', '|---|---|---:|---:|---:|');
const gib = bytes => bytes == null ? 'not established' : (bytes / 2 ** 30).toFixed(2) + ' GiB';
for (const m of memory) md.push(`| ${m.config} | ${m.container} | ${gib(m.peak_bytes)} | ${gib(m.anon_bytes)} | ${gib(m.file_bytes)} |`);
if (faults.length) {
  md.push('', '## A cancel and a lost worker (observed, not gated)', '');
  for (const f of faults) {
    const a = f.after_worker_loss.answer;
    md.push(`- **${f.config}**: ${f.cpus_busy_5s_after_cancel} CPUs busy in the five seconds after the client hung up; with one worker killed the head was ${f.after_worker_loss.head_running ? 'still running' : 'gone'} and the next request ${a.ok ? `answered (${a.chunks} tokens)` : `did not answer (${(a.error ?? `HTTP ${a.status}`).slice(0, 120)})`}.`);
  }
}
md.push('', '## Skipped', '', ...skips.map(s => `- **${s.config}** — ${s.reason}`), '', '## Gates', '', '```', ...out, `== decision: ${decision}`, ...draftDecisions.map(x => `== draft: ${x}`), '```', '');
writeFileSync(join(dir, 'summary.md'), md.join('\n'));
writeFileSync(join(dir, 'summary.json'), JSON.stringify({ meta, rows, loads, memory, faults, skips, gates: out, edge, decision, draftEdges, draftDecisions, failed }, null, 1));

console.log(out.join('\n'));
for (const s of skips) console.log(`  SKIP  ${s.config}: ${s.reason}`);
console.log(`== decision: ${decision}`);
for (const x of draftDecisions) console.log(`== draft: ${x}`);
console.log(`== ${dir}/summary.md`);
process.exit(failed ? 1 : 0);
