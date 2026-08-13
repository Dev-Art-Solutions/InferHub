# Social — v3.21.0

Post manually. **No image needed** — the two-row measurement table is the visual.

Same shape as the 3.20 post: lead with the thing that is true of everybody's code, not with ours.
The hook is the correction, and InferHub is just where it was noticed.

## Facebook

We wrote in our own design notes that uploaded bytes were held in memory. No temp file, no cache.

It was true of every line we wrote. It was false anyway.

ASP.NET's form reader buffers each uploaded file part through a stream that keeps it in memory up to 64 KB — and writes it to a temp file above that. So every real audio upload through our coordinator, for eleven releases, briefly landed on the machine's disk. Not through anything we wrote. Through the framework underneath it.

We found it because we were about to build the thing that makes it unnecessary, and the plan said: check this on a running server first, rather than reading the documentation.

So we checked. A 32 KB upload creates no temp file. A 3 MB upload creates one of 3,145,570 bytes.

Nothing was retained past the request, nothing was logged, no data outlived a call. But the sentence was still wrong, and a wrong sentence in a design note is worse than no sentence — somebody reads it, believes it, and builds on it.

The general version of this, which is why it is worth a post: a claim about YOUR code is not a claim about the REQUEST. Everything between your handler and the socket gets a vote. Frameworks buffer. Proxies retry. Load balancers duplicate. The only way to know what actually happens is to watch the machine while it runs.

InferHub 3.21 makes the temp file unnecessary for large uploads: the body now streams THROUGH the coordinator rather than landing in it. The GPU node pulls it 64 KB at a time, over the connection it already opened, straight into the file its worker is about to read. The hub holds one window and never the whole body.

Two things we wrote down rather than discovered later:

A streamed upload cannot fail over. The bytes are consumed — past the hub, into a node that just died — and a client's socket cannot be rewound. So a node lost mid-upload is an error you have to act on, not a silent retry. That is a real step down in reliability, on a path you opt into by size, and it is in the docs and in the error message.

And the image routes deliberately do NOT get this, because an image job is answered before it runs. Its bytes would have to outlive the request that carried them, which means keeping them on the hub — which is the image store we have refused three times now, for the same reason each time: nobody has agreed to own a retention policy for other people's pictures.

Off by default. Turn it on with one key, and that key moves all three ceilings an upload meets — ours and the two that belong to the web server — because raising one and meeting another with no name attached is a puzzle a design that knew the answer handed you.

https://inferhub.devart.solutions/#idocs_streamed_upload

Blog: https://devart.solutions/blog/inferhub-3-21-we-said-the-bytes-never-touched-disk

## X / Twitter

We wrote in our design notes: uploaded bytes are held in memory, no temp file.

True of every line we wrote. False anyway.

ASP.NET spills any file part over 64 KB to a temp file. Measured: 32 KB → none. 3 MB → one, 3,145,570 bytes.

A claim about your code is not a claim about the request.

---

InferHub 3.21: a large upload now streams THROUGH the coordinator instead of landing in it. The GPU node pulls it 64 KB at a time into the file its worker reads. The hub never holds the body.

Off by default. One key moves all three ceilings — ours and the web server's two.

---

Two things we wrote down instead of discovering later:

1. A streamed upload can't fail over. The bytes are consumed; a socket can't be rewound. Node dies mid-upload → an error you act on, never a silent retry.

2. Image routes deliberately excluded: an image job is answered BEFORE it runs, so its bytes would have to outlive the request. That means storing them. No.

https://inferhub.devart.solutions/#idocs_streamed_upload

Blog: https://devart.solutions/blog/inferhub-3-21-we-said-the-bytes-never-touched-disk

## Notes

- Do not claim a memory profile. We asserted no temp file the size of the upload, not peak RSS.
- Do not post until the published images have been pulled and run.
