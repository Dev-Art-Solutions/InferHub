Vendored from [`beleata74/bg-tts-v5`](https://huggingface.co/beleata74/bg-tts-v5) (MIT licensed), unmodified except
for this notice. `bg_tts_worker.py` imports it the same way `piper_worker.py` imports the `piper` package — the
difference is this one is not on PyPI, so the source is copied in rather than pinned in a requirements file. The
2.9 GB `checkpoint.pt` is fetched separately at image build / first run, exactly as Piper's voices are, and is not
part of this directory.
