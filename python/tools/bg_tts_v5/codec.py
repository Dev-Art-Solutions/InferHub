"""
V4 Codec — NanoCodec 0.6kbps wrapper (identical to V3)
=======================================================
4 codebooks × 4032 codes, 12.5 fps, 50 tok/sec.
Interleaved: [cb0_f0, cb1_f0, cb2_f0, cb3_f0, cb0_f1, ...]
Frame dedup: skip frame if ALL 4 codebooks match previous frame.
"""

import torch
import torchaudio
import numpy as np
from pathlib import Path
from typing import Optional

from .config import (
    NANOCODEC_MODEL_NAME,
    CODEC_SAMPLE_RATE,
    CODEC_NUM_CODEBOOKS,
    CODEC_CODEBOOK_SIZE,
    CODEC_FRAME_RATE,
)


class CodecV5:
    def __init__(self, device: str = "cuda"):
        self.device = device
        self.sample_rate = CODEC_SAMPLE_RATE
        self.num_codebooks = CODEC_NUM_CODEBOOKS
        self.codebook_size = CODEC_CODEBOOK_SIZE
        self.frame_rate = CODEC_FRAME_RATE
        self._load_model()

    def _load_model(self):
        from nemo.collections.tts.models import AudioCodecModel
        self.model = AudioCodecModel.from_pretrained(NANOCODEC_MODEL_NAME).eval()
        self.model.to(self.device)

    @torch.no_grad()
    def encode(self, wav_path: str | Path) -> torch.Tensor:
        """wav → [num_codebooks, num_frames] codes in [0, 4031]."""
        waveform, sr = torchaudio.load(str(wav_path))
        if waveform.shape[0] > 1:
            waveform = waveform.mean(0, keepdim=True)
        if sr != self.sample_rate:
            waveform = torchaudio.functional.resample(waveform, sr, self.sample_rate)
        audio = waveform.squeeze(0).unsqueeze(0).to(self.device)
        audio_len = torch.tensor([audio.shape[-1]], device=self.device)
        tokens, _ = self.model.encode(audio=audio, audio_len=audio_len)
        return tokens.squeeze(0).cpu()

    @torch.no_grad()
    def decode(self, codes: torch.Tensor) -> torch.Tensor:
        """[num_codebooks, num_frames] → waveform [1, samples]."""
        if codes.dim() == 2:
            codes = codes.unsqueeze(0)
        codes = codes.to(self.device)
        tokens_len = torch.tensor([codes.shape[-1]], device=self.device)
        wav, _ = self.model.decode(tokens=codes, tokens_len=tokens_len)
        return wav.cpu()

    def interleave(self, codes: torch.Tensor) -> torch.Tensor:
        """[4, T] → [4*T] interleaved with codebook offsets."""
        C, T = codes.shape
        offsets = torch.arange(C, dtype=codes.dtype).unsqueeze(1) * self.codebook_size
        shifted = codes + offsets
        return shifted.T.contiguous().flatten()

    def deinterleave(self, flat: torch.Tensor) -> torch.Tensor:
        """[4*T] interleaved → [4, T]."""
        flat = flat.cpu()
        C = self.num_codebooks
        T = len(flat) // C
        frames = flat[:T * C].reshape(T, C).T
        offsets = torch.arange(C, dtype=frames.dtype).unsqueeze(1) * self.codebook_size
        return frames - offsets

    def deduplicate_frames(self, interleaved: torch.Tensor) -> torch.Tensor:
        """Remove consecutive identical frames (all 4 codebooks same)."""
        C = self.num_codebooks
        T = len(interleaved) // C
        if T <= 1:
            return interleaved
        frames = interleaved.reshape(T, C)
        keep = [0] + [i for i in range(1, T) if not torch.equal(frames[i], frames[i-1])]
        return frames[keep].flatten()

    def encode_to_tokens(self, wav_path: str, deduplicate: bool = True) -> torch.Tensor:
        """wav → interleaved tokens [0, 16127], optionally deduplicated."""
        codes = self.encode(wav_path)
        flat = self.interleave(codes)
        if deduplicate:
            flat = self.deduplicate_frames(flat)
        return flat

    def tokens_to_wav(self, tokens: torch.Tensor, output: Optional[str] = None) -> torch.Tensor:
        """Interleaved tokens → decoded waveform."""
        codes = self.deinterleave(tokens)
        wav = self.decode(codes)
        if output:
            # DEVIATION FROM UPSTREAM (InferHub, found by running this worker against a real
            # checkpoint, not by reading it): the original line was `wav.squeeze(0)`. `decode()`
            # returns `[batch=1, samples]` — already the `[channels, samples]` torchaudio wants —
            # so squeezing the only leading dim of size 1 collapsed it to a bare `[samples]` 1D
            # tensor, and this torchaudio/soundfile backend refuses to save anything but 2D:
            # `ValueError: Expected 2D Tensor, got 1D.` on every single synthesis. `wav` is used
            # as-is; the guard only fires if some other torchaudio backend ever returns 3D instead.
            to_save = wav if wav.dim() == 2 else wav.squeeze(0)
            # SECOND DEVIATION (same discovery pass): `torchaudio.save` with no `encoding` writes
            # IEEE-float WAV (`wFormatTag == 3`). Python's own `wave` module — which every other
            # InferHub speak worker uses to derive `pcm` from the saved `wav`, and which is what
            # `bg_tts_worker.py` does too — refuses to open that: `wave.Error: unknown format: 3`.
            # Pinning 16-bit PCM is also what Piper's `wave.open(..., "wb")` already produces, so a
            # caller comparing the two workers' `wav` output gets the same container both times.
            torchaudio.save(output, to_save, self.sample_rate, encoding="PCM_S", bits_per_sample=16)
        return wav

    def get_stats(self, wav_path: str) -> dict:
        """Get encoding stats for a wav file."""
        codes = self.encode(wav_path)
        raw = self.interleave(codes)
        dedup = self.deduplicate_frames(raw)
        waveform, sr = torchaudio.load(wav_path)
        dur = waveform.shape[1] / sr
        return {
            "duration_sec": dur,
            "raw_tokens": len(raw),
            "dedup_tokens": len(dedup),
            "compression_pct": (1 - len(dedup) / len(raw)) * 100,
        }
