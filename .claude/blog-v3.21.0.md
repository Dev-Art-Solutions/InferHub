# Blog post — v3.21.0

- **slug**: `inferhub-3-21-we-said-the-bytes-never-touched-disk`
- **title (EN)**: `InferHub 3.21: we said the bytes never touched disk. They did.`
- **DB id**: `6a7d92da4190f6cc234a7d42`
- **published**: 2026-08-13, after the pull-and-run verification. EN visible in one shot,
  BG hidden (the connector is insert-only and the slug locks; a hidden draft cannot be flipped).
- **Cloudflare WAF**: **no shell commands in the body.** The measurement is shown as a table, and
  the request example as request/response JSON. Copy-pasteable `curl` lives on the static site.
- Content is stored **entity-escaped**, like every prior post.
- Run `list_posts` first and confirm the slug is free; **one** `create_post`.

## The angle

**Lead with the correction, not with the feature.** "We added streaming uploads" is a changelog
line. "A claim we made about our own code was false because of the framework underneath it" is a
thing every reader has shipped at least once and mostly has not measured.

The spine:

1. We wrote, in a design note, that uploaded bytes were held in memory — *no temp file, no cache*.
   It was true of every line we wrote.
2. ASP.NET's form reader spills any file part over 64 KB to a temp file. Measured: a 32 KB upload
   creates none; a 3 MB upload creates one of 3 145 570 bytes.
3. So the claim had been wrong since the release that made it. Nothing was retained past the
   request and nothing was logged — no data outlived a call — but the sentence was still false, and
   the fix is to correct it in every place it was written rather than quietly edit it.
4. **The general lesson**: a claim about *your* code is not a claim about the *request*. Everything
   between you and the socket gets a vote, and the only way to know is to watch the machine while
   it runs.

Then the feature, framed as what the correction motivated: the body no longer lands in the hub at
all, because the node pulls it while the client is still sending.

**The second half is the two refusals**, both reusable:

- **A streamed job cannot fail over**, and we say so in the docs and in the error. The body has been
  read; a socket cannot be rewound. Writing that down beats letting somebody find it in an incident.
- **The image routes are deliberately not covered**, and the reason is mechanical rather than
  tiredness: an image job is answered *before* it runs, so its bytes would have to outlive the
  request that carried them — which means storing them, which is the image archive we have now
  refused three times for the same reason each time.

## What it links

- `https://inferhub.devart.solutions/#idocs_streamed_upload` — shipped in the same session.
- `https://github.com/Dev-Art-Solutions/InferHub`

## The measurement table (as a plain block, no shell)

| Upload | ASPNETCORE temp file created |
|---|---|
| 32 KB | none |
| 3 MB | one, 3 145 570 bytes |

## What must NOT go in

- No claim about memory profile — we did not measure peak working set, only that no temp file the
  size of the upload appears.
- No claim about the published images until the pull-and-run has actually been done.
