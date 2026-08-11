# Social — v3.20.0

Post manually. **Counts below are measured** — run the script at the bottom before posting.

This one is different from every other release post: **it is not about InferHub**. It is about a
problem everybody building with coding agents has right now and has not measured. Lead with the
number, not with the product — "our instructions file hit 64k tokens" is the hook, and InferHub is
just where it happened.

**No image needed.** The before/after token table is the visual.

## Facebook

64,000 tokens. That is how big our AI agent's instruction file had become — and it was loaded in full, into every session, before a single question was asked.

InferHub 3.20 is a release that changes nothing about what the product does. No endpoint moved, no behaviour changed, the container images are byte-identical. It fixes something else: the largest recurring cost on the project, which nobody had measured.

Some background. We keep a file agents read before touching the repo — conventions, architecture, and above all the DECISIONS. Why the node runs Python as a subprocess instead of embedding it. Why nothing in the C# ever decodes a pixel. Why the coordinator demotes itself when it cannot reach its database. Sixty releases in, that file was 2,984 lines.

The obvious fix is to trim it. That is wrong.

Every one of those entries records why something is the way it is, usually with the alternative that was rejected and the failure it prevents. We have been burned repeatedly by lost reasoning — somebody "simplifies" a two-line workaround whose comment nobody wrote, and a bug that took a day to find comes back.

The cost is not HAVING the context. It is LOADING it.

So we split it seven ways along the directory tree. An agent working on the Python workers now loads 12,820 tokens instead of 63,930. At the repo root it is 7,740. Every entry point saves between 53% and 87%.

Why the directory tree and not by topic? Because the split has to be one the LOADER can see. Coding agents pick these files up automatically based on where the work is happening. Split by directory and an agent editing the node gets the node's decisions for free. Split by topic and it has to load an index, work out which topics apply — which is exactly what a newcomer does not know — then open three files. That is more tokens than doing nothing.

A split the loader cannot see is a filing system, not a context strategy.

Then the part that actually took the thought. Moving thirty decision blocks between seven files is exactly the operation that silently loses one. Nothing fails. No test goes red. The decision just ceases to exist, and six months later somebody undoes it.

Prose has no compiler. So we wrote one: before anything moved, we generated an inventory of every block, checked it in, and wrote tests asserting each still exists in EXACTLY ONE file, that every cross-reference resolves, and that no file has grown back past its size budget.

That budget is the important bit. It is the only thing that stops the whole exercise being undone one paragraph at a time — which is precisely how the original got to 2,984 lines. Nobody ever added more than a section.

We did the same to the tests: one project of 1,243 tests became four, and the edit loop went from 41 seconds to 2.

Two things it found, and the second is worth stealing whether or not you ever do any of this:

The plan counted 93 test files. There were 124 — a glob shows the top level only, and two subdirectories held another 33.

And CI would have gone green while testing nothing. The workflow named a project path that, after the split, resolves to nothing at all. That does not fail the build. It PASSES, having run zero tests, and reports success.

A build step that can succeed by doing nothing is a build step you do not have.
👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.20.0

## X / Twitter — single post

Our AI agent's instruction file hit 64,000 tokens — loaded in full, every session, before a single question was asked.

The fix isn't "write less". Every entry records why something is the way it is.

The cost isn't having the context. It's loading it.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.20.0

## X / Twitter — thread

**1/5**

Our CLAUDE.md hit 2,984 lines. ~64,000 tokens, loaded in full into every session before a single question was asked.

88% of it was decision history: thirty phases of "why is this like this".

The obvious fix — trim it — is wrong.

**2/5**

Every entry records why something is that way, plus the rejected alternative.

We've been burned by lost reasoning: someone "simplifies" a workaround whose comment nobody wrote, and a day-long bug comes back.

Cost isn't having it. It's loading it.

**3/5**

So: split seven ways along the DIRECTORY TREE.

Not by topic. The split has to be one the loader can see — agents pick these up based on where work happens.

Split by topic and you load an index, guess which topics apply, open three files. More tokens than doing nothing.

**4/5**

Moving 30 decision blocks between 7 files is exactly what silently loses one. Nothing fails. No test goes red.

Prose has no compiler, so we wrote one: an inventory checked in BEFORE the move, asserting each block still exists in exactly one file.

Plus a size budget.

**5/5**

We counted 93 test files. There were 124 — a glob only sees the top level.

CI would've gone green testing NOTHING — it named a path that no longer resolves. That doesn't fail. It passes, zero tests run.

A step that can succeed by doing nothing isn't one.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.20.0

## The count check

Save as `xcount.py` and run `py xcount.py social-v3.20.0.md`. **Use `py`, not `python`** — on this
box `python` is on neither the PowerShell nor the Git-Bash PATH, and `py` is the launcher that is.

```python
"""Measure the X posts against the 280 limit (t.co counts every link as 23, whatever its length)."""

import re
import sys

raw = open(sys.argv[1], encoding="utf-8").read()
LINK = re.search(r"https://github\.com/\S+/releases/tag/\S+", raw).group(0)
cost = lambda t: len(t) - (len(LINK) - 23) * t.count(LINK)

single = raw[raw.index("## X / Twitter — single post"):raw.index("## X / Twitter — thread")]
s = single.split("\n", 1)[1].strip()
print("single: %3d  %s" % (cost(s), "OK" if cost(s) <= 280 else "OVER by %d" % (cost(s) - 280)))

thread = raw[raw.index("## X / Twitter — thread"):raw.index("## The count check")]
for i, p in enumerate(re.split(r"\*\*\d/5\*\*[^\n]*\n", thread)[1:], 1):
    p = p.strip()
    print("%d/5   : %3d  %s" % (i, cost(p), "OK" if cost(p) <= 280 else "OVER by %d" % (cost(p) - 280)))
```

Measured, 2026-08-11 — every one of them rewritten at least once to get here:

```
single: 276  OK
1/5   : 230  OK
2/5   : 248  OK
3/5   : 271  OK
4/5   : 268  OK
5/5   : 280  OK
```
