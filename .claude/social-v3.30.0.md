# Social copy — v3.30.0

Post by hand. Blog post: <https://blog.devart.solutions/blog/inferhub-3-30-two-bugs-that-were-never-about-openrouter>
Release: <https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.30.0>

---

## Facebook

InferHub 3.30 adds OpenRouter, and it cost no new dialect at all.

That is the point rather than the excuse. OpenRouter speaks OpenAI's wire format, so the same client
object serves both and nothing new was written to talk to them.

Which raises a fair question: you could already point a generic `openai-compatible` provider at their
base URL and have it work. So what is the release for?

Everything about them that is *not* the dialect.

Your model map is now checked at boot. Every OpenRouter model id is `vendor/model` — optionally with
a `~` prefix for a floating alias and a `:free` or `:batch` suffix. So `"fast": "gpt-4o-mini"` is a
line that looks completely reasonable and cannot work: that is a real OpenAI id and has never been an
OpenRouter one. Left alone, it is a 400 you find weeks later, on the one request your fleet could not
serve — the worst possible moment. Now it is a startup failure that names the value and the shape.

And then the part I want to be explicit about.

OpenRouter accepts two optional headers that identify your app on their **public** rankings pages. It
would have been easy, and mildly useful to us, to fill them in with InferHub's own name and URL by
default — every deployment that configured a provider would then have quietly contributed to a public
leaderboard entry for this project.

That is free marketing paid for with somebody else's infrastructure appearing on a vendor's public
page because they configured a model. Both fields are absent by default and nothing is sent when they
are absent. If you want to be listed, say so.

**Then the part that made the release worth doing.**

This project has a rule for adding a vendor: read their current documentation on the day, do not
assume the compatibility layer holds. It turned up two bugs, and neither was about OpenRouter.

An error `code` is a *number* there and a *string* at OpenAI. Our envelope declared it a string, so
parsing threw, the code caught its own exception and fell back to printing the raw response body —
the one sentence telling you what to fix, delivered buried in the JSON it arrived in.

And an error that happens *after* the response headers ended the stream quietly. A request that died
at token 40 came back as **200, and looked finished**. A truncated answer presented as a complete one
is the worst shape a failure can take, because nobody goes looking.

Both had been live for *every* OpenAI-compatible upstream since 2.4 — vLLM, LM Studio, TGI, OpenAI
itself. Both are fixed for all of them. Adding a vendor found them; neither belonged to that vendor.

No live provider was called by anything in this release — recorded payloads only, and both fixes were
confirmed to fail without the fix before being kept. Zero new dependencies.

Next: Anthropic's own `/v1/messages`.

---

## X / Twitter

**Thread**

1/ InferHub 3.30 adds OpenRouter, and it cost no new dialect.

That is the point, not the excuse. OpenRouter *is* OpenAI's wire format — same client object, nothing
new written to talk to them.

So what is the release for?

2/ Everything about them that is not the dialect.

You already could point a generic `openai-compatible` provider at their URL and have it work. What
you could not do is find out at boot that your map was wrong.

3/ Every OpenRouter model id is `vendor/model` — optionally `~`-prefixed for a floating alias,
`:free` / `:batch` suffixed.

So `"fast": "gpt-4o-mini"` looks fine and cannot work. Real OpenAI id. Never an OpenRouter one.

4/ Left alone that is a 400 you discover weeks later, on the one request your fleet could not serve.

Now it is a startup failure naming the value and the shape.

Shape check only — validating against their live /models listing would make your boot depend on their
uptime.

5/ OpenRouter takes two optional headers that list your app on their PUBLIC rankings.

Easy to default them to InferHub's own name and URL. Every deployment would then quietly feed a
leaderboard entry for us.

Absent by default. If you want to be listed, say so.

6/ Now the part that made the release worth doing.

Rule here: read the vendor's docs on the day, do not assume the compatibility layer holds.

It found two bugs. Neither was about OpenRouter.

7/ `error.code` is a NUMBER there and a STRING at OpenAI.

Our envelope declared it a string → parse threw → the code caught its own exception → fell back to
the raw body.

The one sentence saying what to fix, buried in the JSON it came in.

8/ And an error arriving AFTER the response headers ended the stream quietly.

Request died at token 40. Came back 200. Looked finished.

A truncated answer presented as complete is the worst shape a failure can take — nobody goes looking.

9/ Both live for every OpenAI-compatible upstream since 2.4. vLLM, LM Studio, TGI, OpenAI itself.

Both fixed for all of them.

Adding a vendor found them. Neither belonged to that vendor.

10/ No live provider was called. Recorded payloads — a captured 429, a captured mid-stream error
frame — and both fixes were confirmed to FAIL without the fix before being kept.

Zero new dependencies. Next: Anthropic's own /v1/messages.

---

## The one-liner, if only one thing gets posted

Adding OpenRouter cost no new dialect and found two bugs that had been true of every
OpenAI-compatible upstream since 2.4: an error code that is a number instead of a string turned every
failure into a wall of raw JSON, and an error arriving mid-stream ended the response as a 200 that
looked finished.
