# Social — v3.24.0

Post manually. **No image.** The visual for this release would be a screenshot of a 404, which is a
joke that needs a paragraph, or a directory listing, which is nothing. The words carry it.

The hook is the ambiguity, not the feature: *a 404 that also means "you made that up"*. Everything
after it is a list of refusals, which is what the release actually is.

Blog: https://devart.solutions/blog/inferhub-3-24-what-durability-may-not-do

## Facebook

Restarting our coordinator used to be indistinguishable from lying to a client.

You submit an image render, get a job id back, and wait ninety seconds. In that window somebody deploys. You come back, ask for your job, and get a 404.

The catch is that it is exactly the same 404 you would get for somebody else's job id. It has to be — a job id is a capability, and answering "that exists but is not yours" is how tenant boundaries leak. So one sentence covered two situations, and the client had to guess which. It guesses wrong. "Your picture is gone" and "you made that id up" produce very different bug reports, and only one of them is our fault.

This release lets a finished job survive a restart. That part is a file write. Everything that took the time was deciding what durability is *not* allowed to do.

**It may not extend retention.** Results live five minutes, read or not. The window is now applied when the hub loads its jobs back — so a hub that was down for an hour deletes everything past the window before it serves a single request, instead of resurrecting it and letting a background timer catch up seconds later. That sounds pedantic. It is the piece we spent the most time on: a five-second window where a week-old picture is fetchable is a retention policy that is wrong for five seconds, and the failure would live in the crash-recovery path, on a box nobody watches, looking exactly like the feature working.

**It may not survive being read.** Fetching an image consumes it. So delivery unlinks the file in the same operation that drops the bytes — because "the picture is gone" being true in the API and false on the disk for a few minutes is the worst version of a privacy claim.

**It may not resume your job — because we will not write down your prompt.** A render that was interrupted mid-flight cannot be picked up again, and not because that would be hard. Re-dispatching it would require having stored what to render: your prompt, your negative prompt, the picture you uploaded. A prompt is not metadata about a request, it is what somebody wanted, and the picture is the answer. Nothing here has ever logged one, and this is the first release where the hub has a directory at all — so the refusal had to become structural: the stored record has no field that could hold a prompt, and there is deliberately no flag to add one, because a field is an invitation. Your job comes back marked failed, saying the hub restarted and it was not resumed. Submitting it again is your call.

**It may not go in Postgres**, where two of our other persistence options can point. Half a gigabyte of PNGs in a bytea column is write-ahead-log amplification on every render and a database dump of your usage ledger that now contains pictures. Symmetry is not a reason to put the wrong thing in a database.

And it is off by default, because switching it on is not a performance decision — it is answering "where are my pictures kept", which somebody then owns.

One thing worth stealing regardless of what you are building: a typo in that setting fails startup and names the key, rather than falling back to "off". A silent fallback there would drop every job on the next restart, which is exactly the failure somebody turned the setting on to prevent. Silent fallbacks are always most expensive in the configuration somebody chose deliberately.

https://inferhub.devart.solutions

Blog: https://devart.solutions/blog/inferhub-3-24-what-durability-may-not-do

## X / Twitter

**One post, not a thread.** Counted: 250 characters of text plus a link (X counts any link as 23
whatever its length) and the two newlines, so **275 of 280**.

A restart turned your job id into a 404 — the same 404 a stranger's id gets. "It's gone" and "you made that up", one sentence.

v3.24 makes image jobs durable. The hard part was what durability may NOT do: extend retention, survive a read, resume it.

https://inferhub.devart.solutions
