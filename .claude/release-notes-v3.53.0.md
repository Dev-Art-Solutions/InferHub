# InferHub v3.53.0 — a 360° panorama, back as a cubemap

Since v3.17 `qwen-360` renders 360° panoramas: 2:1, equirectangular, longitude across and latitude
down. That is the right format for a 360° photo viewer and the wrong one for most other things that
use a sky. Unity, Godot, three.js' `CubeTextureLoader`, Babylon's `CubeTexture` and KTX/DDS cube
textures all want **six square faces**. Until now you converted the panorama yourself, with a
library, and got the face order or the orientation wrong at least once, because each tool documents
its convention somewhere different.

v3.53 does the conversion on the node, when you ask for it.

## What's new

```sh
curl http://localhost:5080/v1/images/generations \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -H 'X-InferHub-Image-Reproject: cubemap' \
  -d '{"model":"qwen-360","prompt":"a lighthouse at dusk","size":"2048x1024"}'

# → 200  "projection": "cubemap", "size": "2048x1024" — and the PNG is 3072x512
```

- **One PNG with six square faces in a row: `+X −X +Y −Y +Z −Z`.** That is the OpenGL cube-map order,
  which three.js, Babylon, KTX and DDS also use. Each face is `width / 4` on a side (512 for a
  2048×1024 render) and is oriented as the OpenGL cube-map table defines it. The panorama's centre
  column faces +X, the same origin three.js uses for its own equirectangular textures, so the join
  lands on the vertical centre line of −X.
- **`size` stays the size of the render.** The steps ran on the 2048×1024 render, and that is what
  is metered and billed. The same render costs the same with or without the header. `"projection":
  "cubemap"` is what tells you the bytes are a `6·(w/4) × (w/4)` strip. It appears in the response,
  on the job document, and as `X-InferHub-Image-Projection` on the content route.
- **The seam comes first.** `seam_delta` and a seam repair still describe the panorama the faces
  were cut from. So `X-InferHub-Image-Seam-Repair: blend` together with `cubemap` closes the join
  and then cuts the cube.
- **Refusals, before any GPU time is spent.** A flat recipe is a `400` ("cannot be cut into a
  cubemap"), because a flat picture is not a sphere. An unknown value is a `400` at the edge that
  names `cubemap`. Both work the same through a coordinator and in solo mode.
- **If the cut itself fails, you still get your picture.** The node returns the panorama, declares
  it `equirectangular`, and adds a `reproject` warning. A two-minute render is never thrown away
  because of the last step, and the response always says what the bytes really are.
- **No new config.** The cut takes about a second of CPU at the largest size, with no VRAM and no
  steps, so there is nothing an operator needs to limit. A request without the header gets the same
  response and the same worker payload as v3.52, byte for byte.
- Works on the edit route too. The catalogue has no equirectangular editor today, so this matters
  only for the future.

The console's 360° viewer shows a `cubemap` job flat, with its "not a panorama" note. That is
accurate for a strip.

## How it is tested

The tests run **the shipped worker's own functions**, not a C# copy of them, on a
*direction-coded* panorama: every pixel's colour is its own view direction. A cube pixel's colour
therefore decodes back to where it was sampled from, which turns "is the orientation right?" into a
comparison with the OpenGL table. The tests check each face's centre, where each face's top and
right edges point, and that the join lands on −X only. Before trusting that test, I mirrored the +X
face on purpose, and it failed exactly the orientation test.

These tests need numpy and Pillow. CI now installs both, so the tests **run** there instead of being
skipped. Locally, `INFERHUB_TEST_PYTHON` points the tests at an interpreter that has them.

Test counts: Shared 202, Node 236, Coordinator 798, Mesh 460, and CI green on the phase commit with the six numpy tests run, not skipped. Zero new `PackageReference`s.

## Also in this release

- **The v3.52.0 fetch-release race is fixed** (`03ffaba`, on `main` since the day after v3.52.0). In
  the published v3.52.0 image, a diffusion worker that had finished its weight download could stay
  idle, holding its CUDA context, until the tool's next request. Models were always declared. v3.53.0
  is the first image with the fix.

## Checked on the published image

Both images carry `org.opencontainers.image.revision` `7f9b526`, the tag commit.

- **`inferhub-node:3.53.0-diffusion`, no GPU.** The worker file that shipped
  (`/opt/inferhub/tools/diffusion_worker.py`) was imported with the image's own Python, numpy 2.5.3
  and Pillow 11.3.0, and given direction-coded panoramas. 2048×1024 → 3072×512 in 563 ms,
  1536×768 → 2304×384 in 274 ms, 1024×512 → 1536×256 in 108 ms. All six face centres decoded to
  their exact axes (largest error 0.0). The flat-recipe and unknown-value refusals both answered
  `invalid_request` with their sentences, and a 512×512 input was declined rather than raised.
- **`inferhub-node:3.53.0`, solo mode.** With no header, `cubemap` and `off`, `/v1/images/generations`
  answered the same `503 capability_unavailable` (this image has no diffusion tool), so a valid value
  passes the edge unchanged. `cross` answered `400` with `param: "X-InferHub-Image-Reproject"` and
  the sentence naming `cubemap`.

**It found one thing, fixed on `main` after the tag.** Pillow 11.3.0, the version the image pins,
warns that `Image.fromarray(..., mode=...)` is removed in Pillow 13. The new cut used that
argument, and so did two older lines (seam blend and the seam-repair mask). Nothing is broken in
v3.53.0: the pin holds, and the warning only goes to the worker's log. But the worker falls back
quietly when the cut fails (by design, so a render is never lost), so a future Pillow bump would
have turned every cubemap into a panorama with a `reproject` warning, and no test would have
noticed. Pillow 12.2, which the tests ran under locally, does not warn at all. The fix drops the
argument (the array's shape already says RGB or L). The tests now treat a deprecation as an error,
and CI installs the **pinned** Pillow instead of the newest. The old code fails 4 of 6 tests under
11.3.0, and the fixed code passes. No v3.53.1 is cut for it, since nothing a caller sees changes.

## Not established, said out loud

- **A real `qwen-360` render cut into a cube on a real card.** That recipe needs about 19.5 GB of
  VRAM. The cut is plain numpy on the finished image, so it was run on synthetic panoramas instead,
  both from source and inside the published image.
- **A strip opened in a real engine or viewer.** The orientation is checked against the OpenGL table
  by tests, not by looking at it in Unity, Godot or three.js.
