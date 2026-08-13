# Blog post — v3.22.0

- **slug**: `inferhub-3-22-we-stopped-writing-plans-in-advance`
- **title (EN)**: `InferHub 3.22: we stopped writing plans in advance`
- **published**: 2026-08-13. EN visible in one shot, BG hidden (the connector is insert-only and
  the slug locks; a hidden draft cannot be flipped).
- **Cloudflare WAF**: no shell commands in the body. Nothing here needs one.
- Content stored **entity-escaped**, like every prior post.
- `list_posts` run first; slug confirmed free; **one** `create_post`.

## The angle

**Lead with the counterintuitive half, not with the tidy-up.** "We reorganised our planning docs"
is a chore. "We stopped writing plans in advance, and a document we wrote two days early got six
things wrong" is a claim with a number behind it — and every reader has a planning doc that aged.

The spine:

1. v3.20 fixed the file an agent reads. It left alone the files that *produce* the work: 519 lines
   for one phase, 162 KB for one track.
2. Three failures, and only the first is about size. §6 was in the wrong document — verification
   results appended to a plan after the release, which is what release notes are. The brief
   re-argued the rules, so an amended rule left stale copies. The format itself lived in the root
   file: 52 lines taxing every session, most of which never write a plan.
3. **The finding**: a brief written ahead of its phase is mostly prediction. Phase 53's carries a
   section listing six things it got wrong — and we only know because it wrote them down. So a
   brief is now written on the day, and a track file is an index rather than a container.
4. Prose has no compiler, so three checks: a line budget, a marker every brief must carry, and an
   assertion that a brief's status matches the index row. **The status one is the only one that has
   ever drifted in practice** — the brief gets flipped at the end of a release and the table is the
   step somebody skips.
5. **The honest ending**: the briefs stay private, so those checks run on one machine and are
   *skipped* in CI, not passed. A green tick from a job that has never seen the directory means
   nothing, and saying so is cheaper than discovering it later.

## The numbers that may be used

| | Before | After |
|---|---:|---:|
| root `CLAUDE.md` | 395 lines | **339** |
| a single phase brief | 519 (phase 53) | **171** / **185** (54 / 55) |
| lean-brief budget | — | 250, enforced |

## What must NOT go in

- No token counts for the plan folder — lines and bytes were measured, tokens were not.
- No claim that CI verifies the plans. It cannot see them.
- Nothing about phases 55–60 as *shipped*; they are decided and written, which is a different word.
