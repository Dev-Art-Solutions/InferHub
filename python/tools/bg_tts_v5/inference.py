"""
V5 Inference — encoder-decoder TTS generation
===============================================
1. Encode text with encoder (bidirectional, once)
2. Cache encoder KV for cross-attention
3. Autoregressively decode audio tokens with decoder
"""

import torch
import argparse
from .config import (
    AUDIO_OFFSET, TOTAL_VOCAB_SIZE, END_OF_SPEECH_TOKEN_ID,
    START_OF_SPEECH_TOKEN_ID, CODEC_NUM_CODEBOOKS, NUM_AUDIO_TOKENS,
    TOKENS_PER_FRAME,
)
from .tokenizer import TTSTokenizer
from .codec import CodecV5
from .model import load_for_inference


@torch.no_grad()
def generate(model, tokenizer, text, speaker_id=0, max_new_tokens=2000,
             temperature=0.7, top_k=250, top_p=0.95, rep_penalty=1.1, device="cuda"):
    """
    Generate audio tokens from text using encoder-decoder model.
    Supports frame-level position encoding when model.decoder.tokens_per_frame > 1.
    """
    # 1. Encode text (one shot, bidirectional)
    enc_ids = tokenizer.build_encoder_input(text, speaker_id).unsqueeze(0).to(device)
    enc_mask = torch.ones_like(enc_ids)
    
    # Run encoder + projection (cached for all decoder steps)
    enc_out = model.encode(enc_ids, enc_mask)  # [1, T_enc, dec_d]
    
    # 2. Start decoder with <sos>
    dec_ids = torch.tensor([[START_OF_SPEECH_TOKEN_ID]], device=device)
    past = None
    generated_tokens = []

    for step in range(max_new_tokens):
        # Run decoder step
        inp = dec_ids[:, -1:] if past is not None else dec_ids
        dec_out = model.decoder(
            input_ids=inp,
            encoder_output=enc_out,
            encoder_mask=enc_mask,
            past_key_values=past,
            use_cache=True,
        )
        past = dec_out["past_key_values"]
        logits = dec_out["logits"][:, -1, :]

        # Mask: only audio tokens + end_of_speech
        mask = torch.full_like(logits, float("-inf"))
        mask[:, AUDIO_OFFSET:AUDIO_OFFSET + NUM_AUDIO_TOKENS] = 0
        mask[:, END_OF_SPEECH_TOKEN_ID] = 0
        logits = logits + mask

        # Repetition penalty on recent tokens
        if rep_penalty != 1.0 and generated_tokens:
            recent = set(generated_tokens[-200:])
            for tid in recent:
                if AUDIO_OFFSET <= tid < AUDIO_OFFSET + NUM_AUDIO_TOKENS:
                    logits[:, tid] /= rep_penalty

        logits = logits / temperature

        # Top-k
        if top_k > 0:
            kth = torch.topk(logits, min(top_k, logits.shape[-1])).values[:, -1:]
            logits[logits < kth] = float("-inf")

        # Top-p
        if top_p < 1.0:
            sorted_l, sorted_i = torch.sort(logits, descending=True)
            cum = torch.cumsum(torch.softmax(sorted_l, -1), -1)
            remove = cum > top_p
            remove[:, 1:] = remove[:, :-1].clone()
            remove[:, 0] = False
            logits[remove.scatter(1, sorted_i, remove)] = float("-inf")

        next_tok = torch.multinomial(torch.softmax(logits, -1), 1)
        tok_id = next_tok.item()

        if tok_id == END_OF_SPEECH_TOKEN_ID:
            break

        generated_tokens.append(tok_id)
        dec_ids = torch.cat([dec_ids, next_tok], dim=-1)

    if not generated_tokens:
        return None

    result = torch.tensor(generated_tokens, dtype=torch.long)
    # Extract raw audio indices
    audio_mask = (result >= AUDIO_OFFSET) & (result < AUDIO_OFFSET + NUM_AUDIO_TOKENS)
    return result[audio_mask] - AUDIO_OFFSET


def synthesize(checkpoint, text, output="output.wav", speaker_id=0,
               temperature=0.7, top_k=250, top_p=0.95, rep_penalty=1.1,
               max_tokens=2000, device="cuda"):
    print(f"🎤 '{text[:80]}' | spk={speaker_id} | T={temperature}")
    model = load_for_inference(checkpoint, device=device)
    tokenizer = TTSTokenizer()
    codec = CodecV5(device=device)

    tokens = generate(model, tokenizer, text, speaker_id, max_tokens,
                      temperature, top_k, top_p, rep_penalty, device)
    if tokens is None or len(tokens) == 0:
        print("❌ No audio generated!"); return

    tokens = tokens[:len(tokens) - len(tokens) % CODEC_NUM_CODEBOOKS]
    print(f"🔊 {len(tokens)} tokens ({len(tokens)//4} frames, ~{len(tokens)//4/12.5:.1f}s)")

    wav = codec.tokens_to_wav(tokens, output)
    print(f"✅ {output} ({wav.shape[1]/22050:.2f}s)")
    return wav


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--checkpoint", required=True)
    p.add_argument("--text", required=True)
    p.add_argument("--output", default="output.wav")
    p.add_argument("--speaker", type=int, default=0)
    p.add_argument("--temperature", type=float, default=0.7)
    p.add_argument("--top-k", type=int, default=250)
    p.add_argument("--top-p", type=float, default=0.95)
    p.add_argument("--rep-penalty", type=float, default=1.1)
    p.add_argument("--max-tokens", type=int, default=2000)
    a = p.parse_args()
    synthesize(a.checkpoint, a.text, a.output, a.speaker,
               a.temperature, a.top_k, a.top_p, a.rep_penalty, a.max_tokens)

if __name__ == "__main__":
    main()
