"""
V5 Config — Encoder-Decoder TTS with Cross-Attention + CTC Loss
================================================================
Same vocab layout as V4:
  [0..8]        = 9 special tokens
  [9..154]      = ~146 text chars (BG + EN + digits + punct)
  [155..16282]  = 16,128 audio tokens (4 codebooks × 4032)
  Total ≈ 16,283

Architecture change: separate text encoder + audio decoder with cross-attention.
"""

# ── NanoCodec 0.6kbps (same as V2/V3/V4) ──────────────────────
NANOCODEC_MODEL_NAME = "nvidia/nemo-nano-codec-22khz-0.6kbps-12.5fps"
CODEC_SAMPLE_RATE    = 22_050
CODEC_NUM_CODEBOOKS  = 4
CODEC_CODEBOOK_SIZE  = 4_032
CODEC_FRAME_RATE     = 12.5
CODEC_TOKENS_PER_SEC = 50  # 12.5 × 4
TOKENS_PER_FRAME     = 4   # 4 codebook tokens per audio frame

# ── Character set (identical to V4) ────────────────────────────
BG_LOWER  = "абвгдежзийклмнопрстуфхцчшщъьюя"
BG_UPPER  = "АБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЬЮЯ"
EN_LOWER  = "abcdefghijklmnopqrstuvwxyz"
EN_UPPER  = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
DIGITS    = "0123456789"
PUNCT     = '.,!?;:-–—…"\'()[]{}«»„"" '
EXTRA     = "\n\t"

_ALL_CHARS: list[str] = []
_seen: set[str] = set()
for _src in [BG_LOWER, BG_UPPER, EN_LOWER, EN_UPPER, DIGITS, PUNCT, EXTRA]:
    for _ch in _src:
        if _ch not in _seen:
            _ALL_CHARS.append(_ch)
            _seen.add(_ch)

# ── Special tokens (indices 0..8) ──────────────────────────────
SPECIAL_TOKENS = {
    "<pad>":             0,
    "<start_of_text>":   1,
    "<end_of_text>":     2,
    "<start_of_speech>": 3,
    "<end_of_speech>":   4,
    "<spk_0>":           5,
    "<spk_1>":           6,
    "<spk_2>":           7,
    "<spk_3>":           8,
}
NUM_SPECIAL_TOKENS = len(SPECIAL_TOKENS)     # 9

# ── Vocab offsets ───────────────────────────────────────────────
TEXT_CHARS       = _ALL_CHARS
TEXT_VOCAB_SIZE  = len(TEXT_CHARS)             # ~146
TEXT_OFFSET      = NUM_SPECIAL_TOKENS         # 9
AUDIO_OFFSET     = TEXT_OFFSET + TEXT_VOCAB_SIZE  # 155
NUM_AUDIO_TOKENS = CODEC_NUM_CODEBOOKS * CODEC_CODEBOOK_SIZE  # 16,128
TOTAL_VOCAB_SIZE = AUDIO_OFFSET + NUM_AUDIO_TOKENS            # 16,283

# Only the decoder needs audio vocab. Encoder vocab = special + text = 155
ENCODER_VOCAB_SIZE = AUDIO_OFFSET  # 155 (special + text only)
DECODER_VOCAB_SIZE = TOTAL_VOCAB_SIZE  # 16,283 (full)

# ── Convenience IDs ─────────────────────────────────────────────
PAD_TOKEN_ID             = SPECIAL_TOKENS["<pad>"]
START_OF_TEXT_TOKEN_ID    = SPECIAL_TOKENS["<start_of_text>"]
END_OF_TEXT_TOKEN_ID      = SPECIAL_TOKENS["<end_of_text>"]
START_OF_SPEECH_TOKEN_ID  = SPECIAL_TOKENS["<start_of_speech>"]
END_OF_SPEECH_TOKEN_ID    = SPECIAL_TOKENS["<end_of_speech>"]
SPK_0_TOKEN_ID            = SPECIAL_TOKENS["<spk_0>"]
SPK_1_TOKEN_ID            = SPECIAL_TOKENS["<spk_1>"]

# ── Helper functions ────────────────────────────────────────────
def audio_token_id(codebook: int, code: int) -> int:
    return AUDIO_OFFSET + codebook * CODEC_CODEBOOK_SIZE + code

def decode_audio_token(token_id: int) -> tuple[int, int]:
    offset = token_id - AUDIO_OFFSET
    return offset // CODEC_CODEBOOK_SIZE, offset % CODEC_CODEBOOK_SIZE

def is_audio_token(token_id: int) -> bool:
    return AUDIO_OFFSET <= token_id < AUDIO_OFFSET + NUM_AUDIO_TOKENS

def is_special_token(token_id: int) -> bool:
    return 0 <= token_id < NUM_SPECIAL_TOKENS

def is_text_token(token_id: int) -> bool:
    return TEXT_OFFSET <= token_id < AUDIO_OFFSET

# ── V5 Model Config ────────────────────────────────────────────
# Encoder: 6 bidirectional layers (text is short, 50-200 chars)
ENC_D_MODEL    = 512
ENC_N_HEADS    = 8
ENC_N_LAYERS   = 6
ENC_D_FF       = 2048

# Decoder: 18 causal layers (audio generation, the heavy part)
DEC_D_MODEL    = 768
DEC_N_HEADS    = 12
DEC_N_LAYERS   = 18
DEC_D_FF       = 3072       # Standard 4x

MAX_TEXT_LEN   = 512         # Max text tokens (chars)
MAX_AUDIO_LEN  = 2048        # Max audio tokens (decoder sequence)
DROPOUT        = 0.10        # Increased from V4's 0.05, matching V2

# CTC auxiliary loss
CTC_WEIGHT     = 0.1         # Weight for CTC loss (main CE = 1.0)

# ── Training defaults ──────────────────────────────────────────
BATCH_SIZE     = 8
GRAD_ACCUM     = 2           # effective = 16
LR             = 3e-4
WEIGHT_DECAY   = 0.1
WARMUP_STEPS   = 500
NUM_EPOCHS     = 3

# ── Print summary ──────────────────────────────────────────────
if __name__ == "__main__":
    print(f"V5 Vocab Layout:")
    print(f"  Special:  [0, {NUM_SPECIAL_TOKENS-1}]  ({NUM_SPECIAL_TOKENS} tokens)")
    print(f"  Text:     [{TEXT_OFFSET}, {AUDIO_OFFSET-1}]  ({TEXT_VOCAB_SIZE} chars)")
    print(f"  Audio:    [{AUDIO_OFFSET}, {TOTAL_VOCAB_SIZE-1}]  ({NUM_AUDIO_TOKENS} tokens)")
    print(f"  TOTAL:    {TOTAL_VOCAB_SIZE}")
    print()
    print(f"V5 Encoder: d={ENC_D_MODEL}, heads={ENC_N_HEADS}, layers={ENC_N_LAYERS}, d_ff={ENC_D_FF}")
    print(f"V5 Decoder: d={DEC_D_MODEL}, heads={DEC_N_HEADS}, layers={DEC_N_LAYERS}, d_ff={DEC_D_FF}")

    # Estimate params
    enc_emb = ENCODER_VOCAB_SIZE * ENC_D_MODEL
    enc_per = 4 * ENC_D_MODEL**2 + 2 * ENC_D_MODEL * ENC_D_FF + 2 * ENC_D_MODEL
    enc_total = enc_emb + ENC_N_LAYERS * enc_per

    dec_emb = DECODER_VOCAB_SIZE * DEC_D_MODEL
    dec_self = 4 * DEC_D_MODEL**2
    dec_cross = 2 * DEC_D_MODEL * ENC_D_MODEL + 2 * DEC_D_MODEL**2  # KV from enc, QO from dec
    dec_ffn = 2 * DEC_D_MODEL * DEC_D_FF
    dec_per = dec_self + dec_cross + dec_ffn + 4 * DEC_D_MODEL
    dec_total = dec_emb + DEC_N_LAYERS * dec_per

    proj = ENC_D_MODEL * DEC_D_MODEL  # encoder projection
    ctc_head = DEC_D_MODEL * DECODER_VOCAB_SIZE  # CTC head (small)

    total = enc_total + dec_total + proj
    print(f"  Encoder:  {enc_total/1e6:.1f}M")
    print(f"  Decoder:  {dec_total/1e6:.1f}M")
    print(f"  Total:    ~{total/1e6:.0f}M")
