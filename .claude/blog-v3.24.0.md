# Blog post — v3.24.0

- **slug**: `inferhub-3-24-what-durability-may-not-do`
- **title (EN)**: `InferHub 3.24: we made image jobs durable by deciding what durability may not do`
- **published**: 2026-08-15. EN visible in one shot, BG hidden (the connector is insert-only and the
  slug locks; a hidden draft cannot be flipped).
- **Cloudflare WAF**: no shell commands in the body at all — a command line in the HTML gets the
  *request* blocked, not the command. Config keys go in prose.
- Content stored **entity-escaped**, like every prior post.
- `list_posts` run first; slug confirmed free; **one** `create_post`.

## The angle

**Lead with the 404 that means two things.** "Image jobs are now durable" is a changelog line. The
argument is: the feature is small and every interesting decision in it is a *refusal* — durability
may not extend retention, may not survive a read, may not resume your job, and may not go in the
database. Each refusal has a failure behind it.

The spine:

1. A job id from thirty seconds ago comes back `404` after a deploy — and it is byte-identical to
   the 404 a stranger's id gets, because that is what the isolation rule requires. The client cannot
   tell "your picture is gone" from "you made that up", and takes the second reading.
2. Fixing that is one file write. **Everything that took the time was deciding what it may not do.**
3. It may not extend retention. The window is applied *on load* — a restart cannot be a way to keep
   a picture longer than you allowed, and the resurrection bug is the one nobody would ever find,
   because it lives in the crash-recovery path and looks like the feature working.
4. It may not survive a read. Delivery unlinks the file in the same operation.
5. **It may not resume your job — because we will not write down your prompt.** There is no field
   for one, deliberately. So an interrupted job comes back failed, saying so.
6. It may not go in Postgres, and under clustering it is per instance. Both said out loud.
7. Off by default, because turning it on is answering a data-retention question.

## Body (plain HTML, escaped on publish)

See the published post.
