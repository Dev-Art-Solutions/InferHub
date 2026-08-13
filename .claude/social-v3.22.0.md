# Social — v3.22.0

Post manually. **No image needed** — the line-count contrast is the visual.

Same shape as 3.20 and 3.21: lead with the thing that is true of everybody's process, not with ours.
The hook is "we stopped writing plans in advance"; InferHub is just where it was measured.

## Facebook

Two releases ago we wrote about our agent instruction file reaching 64,000 tokens — loaded in full into every session before a single question was asked — and splitting it up.

That fixed the file the agent READS. It left alone the files that produce the work: the build plans.

One of those had reached 519 lines. For a single phase. A track file was 162 KB. Nobody noticed, because unlike the instructions file nothing loads all of them at once — so the cost stayed invisible right up until you had to write the next one.

Three things were wrong, and only the first is about size.

The verification results were in the wrong document. Every plan ended with what was observed on the box after the release — the numbers, the host, what did not get run. That is what release notes are. We were writing both.

Every plan re-argued the rules it touched. So when a later phase amended a rule, two files kept saying the old thing. We had already spent a whole release forbidding exactly that between our context files — one home per decision, pointers everywhere else, because two copies drift and the day they disagree the reader believes whichever one their working directory happened to load. The plan folder sat outside that rule.

And the format itself lived in the root file: 52 lines of "how to write a plan", paid for by every session including the overwhelming majority that never write one.

But here is the part we did not expect.

We used to plan a whole track up front — six phases, written before the first one shipped. It reads as thoroughness. It is mostly prediction.

The evidence was already in our repository. The plan for our previous release was written two days ahead of the work, and it carries a section titled "six things the brief got wrong or did not know." We only know that because the format made somebody write it down.

So: a plan is now written the day its phase starts. The track file became an index — the order, the claim per phase, and the point where the track gets cut if it has to be cut. Not six documents about a future that has not happened yet.

Prose has no compiler, so it is tested now. 250 lines per plan. A marker every plan must carry. And an assertion that a plan's status matches its row in the index — which is the only one of the three that has ever actually drifted, for a completely mundane reason: the plan gets flipped to done at the end of a release, and the table is the step somebody skips.

One detail worth stealing: the marker lives in the DOCUMENT, not in a list inside the test. A list is a second place to update, and forgetting it fails silently — the file is simply never checked. Forgetting a marker fails on a screen. Always prefer the failure mode that shouts.

And we made the check fail on purpose before trusting it. A check that has drifted away from its subject is worse than no check, because it reads as coverage.

Last thing, because it costs one sentence to be honest: our plans are not in the public repository, so those three checks run on one machine and are SKIPPED in CI, not passed. A green tick from a job that has never seen the directory means nothing.

Nothing else changed in this release. No endpoint moved, no image was rebuilt. A release that changes no behaviour should say so in its first clause rather than dressing a reorganisation up as a feature.

https://inferhub.devart.solutions

Blog: https://devart.solutions/blog/inferhub-3-22-we-stopped-writing-plans-in-advance

## X / Twitter

**One post, not a thread** (measured: 240 chars of text + a link, which X counts as 23 whatever its
length — 265 of 280). The thread version was written first and cut: the hook stands alone, and the
two follow-ups were the blog post's job.

The quote is a paraphrase, not quotation marks, deliberately. The real section title is *"Six things
the brief got wrong or did not know"* and it would not fit; **trimming text inside quote marks to
make it fit is a misquote**, so the marks come off instead.

We planned whole tracks up front — six phases written before the first shipped. Reads as thoroughness; mostly prediction.

Our last release's plan was written two days early. It ends with a list of six things it got wrong or did not know.

https://devart.solutions/blog/inferhub-3-22-we-stopped-writing-plans-in-advance
