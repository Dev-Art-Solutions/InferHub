"""
V5 Model — Encoder-Decoder TTS with Cross-Attention + CTC Loss
================================================================
Architecture:
  - Text Encoder: 6-layer bidirectional Transformer (d=512, 8 heads)
  - Audio Decoder: 18-layer causal Transformer (d=768, 12 heads)
    with cross-attention to encoder output at every layer
  - CTC auxiliary head on encoder output for alignment guidance
  - Projection layer: enc_dim → dec_dim

Why encoder-decoder > decoder-only for TTS:
  1. Encoder sees full text bidirectionally → better grammar understanding
  2. Cross-attention at every decoder layer → text always "visible"
  3. CTC loss guides encoder to learn monotonic alignment
  4. Decoder focuses 100% on audio generation, no text memorization burden

Target: ~200M params total (V2-proven size range)
  Encoder: ~19M, Decoder: ~170M, Projection: ~0.4M, CTC head: ~0.3M
"""

import math
import os
import torch
import torch.nn as nn
import torch.nn.functional as F
from typing import Optional, Tuple, Dict
from dataclasses import dataclass

from .config import (
    TOTAL_VOCAB_SIZE, ENCODER_VOCAB_SIZE, DECODER_VOCAB_SIZE,
    ENC_D_MODEL, ENC_N_HEADS, ENC_N_LAYERS, ENC_D_FF,
    DEC_D_MODEL, DEC_N_HEADS, DEC_N_LAYERS, DEC_D_FF,
    MAX_TEXT_LEN, MAX_AUDIO_LEN, DROPOUT, CTC_WEIGHT,
    PAD_TOKEN_ID, NUM_AUDIO_TOKENS, AUDIO_OFFSET,
)


# ── Shared Components ──────────────────────────────────────────

class RMSNorm(nn.Module):
    def __init__(self, dim: int, eps: float = 1e-6):
        super().__init__()
        self.eps = eps
        self.weight = nn.Parameter(torch.ones(dim))

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return x * torch.rsqrt(x.pow(2).mean(-1, keepdim=True) + self.eps) * self.weight


class RotaryPositionalEmbedding(nn.Module):
    def __init__(self, dim: int, max_seq_len: int = 4096, base: float = 10000.0):
        super().__init__()
        self.dim = dim
        self.max_seq_len = max_seq_len
        inv_freq = 1.0 / (base ** (torch.arange(0, dim, 2).float() / dim))
        self.register_buffer("inv_freq", inv_freq, persistent=False)
        self._build_cache(max_seq_len)

    def _build_cache(self, seq_len: int):
        t = torch.arange(seq_len, dtype=self.inv_freq.dtype)
        freqs = torch.outer(t, self.inv_freq)
        emb = torch.cat((freqs, freqs), dim=-1)
        self.register_buffer("cos_cached", emb.cos(), persistent=False)
        self.register_buffer("sin_cached", emb.sin(), persistent=False)

    def forward(self, seq_len: int) -> Tuple[torch.Tensor, torch.Tensor]:
        if seq_len > self.max_seq_len:
            self._build_cache(seq_len)
            self.max_seq_len = seq_len
        return self.cos_cached[:seq_len], self.sin_cached[:seq_len]


def rotate_half(x: torch.Tensor) -> torch.Tensor:
    x1, x2 = x.chunk(2, dim=-1)
    return torch.cat((-x2, x1), dim=-1)


def apply_rotary_pos_emb(q, k, cos, sin):
    cos = cos.unsqueeze(0).unsqueeze(0)
    sin = sin.unsqueeze(0).unsqueeze(0)
    return (q * cos + rotate_half(q) * sin,
            k * cos + rotate_half(k) * sin)


class SwiGLUFFN(nn.Module):
    def __init__(self, d_model: int, d_ff: int, dropout: float):
        super().__init__()
        self.gate_proj = nn.Linear(d_model, d_ff, bias=False)
        self.up_proj   = nn.Linear(d_model, d_ff, bias=False)
        self.down_proj = nn.Linear(d_ff, d_model, bias=False)
        self.dropout   = nn.Dropout(dropout)

    def forward(self, x):
        return self.dropout(self.down_proj(F.silu(self.gate_proj(x)) * self.up_proj(x)))


# ── Encoder (Bidirectional) ────────────────────────────────────

class EncoderSelfAttention(nn.Module):
    """Bidirectional self-attention for text encoder (NO causal mask)."""
    def __init__(self, d_model: int, n_heads: int, dropout: float, max_len: int):
        super().__init__()
        self.d_model = d_model
        self.n_heads = n_heads
        self.head_dim = d_model // n_heads
        assert d_model % n_heads == 0

        self.q_proj = nn.Linear(d_model, d_model, bias=False)
        self.k_proj = nn.Linear(d_model, d_model, bias=False)
        self.v_proj = nn.Linear(d_model, d_model, bias=False)
        self.o_proj = nn.Linear(d_model, d_model, bias=False)
        self.resid_dropout = nn.Dropout(dropout)
        # No RoPE for encoder — use learned positional embeddings instead
        # (bidirectional + positional = sinusoidal/learned is fine)

    def forward(self, x, key_padding_mask=None):
        B, T, _ = x.shape
        q = self.q_proj(x).view(B, T, self.n_heads, self.head_dim).transpose(1, 2)
        k = self.k_proj(x).view(B, T, self.n_heads, self.head_dim).transpose(1, 2)
        v = self.v_proj(x).view(B, T, self.n_heads, self.head_dim).transpose(1, 2)

        # Bidirectional: is_causal=False
        attn_mask = None
        if key_padding_mask is not None:
            # key_padding_mask: [B, T], True = pad
            attn_mask = key_padding_mask.unsqueeze(1).unsqueeze(2)  # [B, 1, 1, T]
            attn_mask = attn_mask.float() * torch.finfo(q.dtype).min

        attn_out = F.scaled_dot_product_attention(
            q, k, v,
            attn_mask=attn_mask,
            dropout_p=self.resid_dropout.p if self.training else 0.0,
            is_causal=False,
        )
        attn_out = attn_out.transpose(1, 2).contiguous().view(B, -1, self.d_model)
        return self.resid_dropout(self.o_proj(attn_out))


class EncoderBlock(nn.Module):
    def __init__(self, d_model: int, n_heads: int, d_ff: int, dropout: float, max_len: int):
        super().__init__()
        self.attn_norm = RMSNorm(d_model)
        self.attention = EncoderSelfAttention(d_model, n_heads, dropout, max_len)
        self.ffn_norm  = RMSNorm(d_model)
        self.ffn       = SwiGLUFFN(d_model, d_ff, dropout)

    def forward(self, x, key_padding_mask=None):
        x = x + self.attention(self.attn_norm(x), key_padding_mask)
        x = x + self.ffn(self.ffn_norm(x))
        return x


class TextEncoder(nn.Module):
    """
    Bidirectional Transformer encoder for text.
    Input: text token IDs (special + chars, vocab 155)
    Output: contextualized text representations [B, T_text, enc_d_model]
    """
    def __init__(self, vocab_size=ENCODER_VOCAB_SIZE, d_model=ENC_D_MODEL,
                 n_heads=ENC_N_HEADS, n_layers=ENC_N_LAYERS, d_ff=ENC_D_FF,
                 max_len=MAX_TEXT_LEN, dropout=DROPOUT):
        super().__init__()
        self.d_model = d_model
        self.token_embedding = nn.Embedding(vocab_size, d_model, padding_idx=PAD_TOKEN_ID)
        self.pos_embedding = nn.Embedding(max_len, d_model)
        self.embed_dropout = nn.Dropout(dropout)

        self.layers = nn.ModuleList([
            EncoderBlock(d_model, n_heads, d_ff, dropout, max_len)
            for _ in range(n_layers)
        ])
        self.final_norm = RMSNorm(d_model)

    def forward(self, input_ids, attention_mask=None):
        """
        input_ids:      [B, T_text] — text token IDs
        attention_mask:  [B, T_text] — 1 for real, 0 for pad
        Returns:         [B, T_text, d_model] — contextualized representations
        """
        B, T = input_ids.shape
        pos = torch.arange(T, device=input_ids.device).unsqueeze(0)
        h = self.embed_dropout(self.token_embedding(input_ids) + self.pos_embedding(pos))

        key_padding_mask = None
        if attention_mask is not None:
            key_padding_mask = (attention_mask == 0)  # True = pad position

        for layer in self.layers:
            h = layer(h, key_padding_mask)

        return self.final_norm(h)


# ── Decoder (Causal with Cross-Attention) ──────────────────────

class DecoderSelfAttention(nn.Module):
    """Causal self-attention for audio decoder with KV-cache."""
    def __init__(self, d_model: int, n_heads: int, dropout: float, max_len: int,
                 tokens_per_frame: int = 1):
        super().__init__()
        self.d_model = d_model
        self.n_heads = n_heads
        self.head_dim = d_model // n_heads
        self.tokens_per_frame = tokens_per_frame
        assert d_model % n_heads == 0

        self.q_proj = nn.Linear(d_model, d_model, bias=False)
        self.k_proj = nn.Linear(d_model, d_model, bias=False)
        self.v_proj = nn.Linear(d_model, d_model, bias=False)
        self.o_proj = nn.Linear(d_model, d_model, bias=False)
        self.resid_dropout = nn.Dropout(dropout)
        self.rope = RotaryPositionalEmbedding(self.head_dim, max_len)

    def forward(self, x, past_kv=None, use_cache=False, position_ids=None):
        B, T, _ = x.shape
        q = self.q_proj(x).view(B, T, self.n_heads, self.head_dim).transpose(1, 2)
        k = self.k_proj(x).view(B, T, self.n_heads, self.head_dim).transpose(1, 2)
        v = self.v_proj(x).view(B, T, self.n_heads, self.head_dim).transpose(1, 2)

        # RoPE — with optional frame-level position encoding
        if position_ids is not None:
            # Frame-level: position_ids maps each token to its frame position
            max_pos = position_ids.max().item() + 1
            cos, sin = self.rope(max_pos)
            cos = cos[position_ids]  # [T] -> gather by position_ids
            sin = sin[position_ids]
        elif past_kv is not None:
            # KV-cache inference: compute frame-level offset
            kv_len = past_kv[0].shape[2]  # total cached tokens
            if self.tokens_per_frame > 1:
                offset = kv_len // self.tokens_per_frame  # frame-level position
            else:
                offset = kv_len
            cos, sin = self.rope(offset + T)
            cos, sin = cos[offset:offset + T], sin[offset:offset + T]
        else:
            offset = 0
            cos, sin = self.rope(T)

        q, k = apply_rotary_pos_emb(q, k, cos, sin)

        if past_kv is not None:
            k = torch.cat([past_kv[0], k], dim=2)
            v = torch.cat([past_kv[1], v], dim=2)

        new_kv = (k, v) if use_cache else None

        is_causal = (past_kv is None) and (T > 1)
        attn_out = F.scaled_dot_product_attention(
            q, k, v,
            dropout_p=self.resid_dropout.p if self.training else 0.0,
            is_causal=is_causal,
        )
        attn_out = attn_out.transpose(1, 2).contiguous().view(B, -1, self.d_model)
        return self.resid_dropout(self.o_proj(attn_out)), new_kv


class CrossAttention(nn.Module):
    """
    Cross-attention: decoder queries attend to encoder keys/values.
    This is THE key difference from decoder-only — at every layer,
    the decoder can "look at" the full encoded text.
    """
    def __init__(self, dec_d_model: int, enc_d_model: int, n_heads: int, dropout: float):
        super().__init__()
        self.d_model = dec_d_model
        self.n_heads = n_heads
        self.head_dim = dec_d_model // n_heads
        assert dec_d_model % n_heads == 0

        # Q comes from decoder, K/V come from encoder
        self.q_proj = nn.Linear(dec_d_model, dec_d_model, bias=False)
        self.k_proj = nn.Linear(enc_d_model, dec_d_model, bias=False)
        self.v_proj = nn.Linear(enc_d_model, dec_d_model, bias=False)
        self.o_proj = nn.Linear(dec_d_model, dec_d_model, bias=False)
        self.resid_dropout = nn.Dropout(dropout)

    def forward(self, x, encoder_output, encoder_mask=None, cached_kv=None, use_cache=False):
        """
        x:              [B, T_dec, dec_d]  — decoder hidden states
        encoder_output: [B, T_enc, enc_d]  — encoder output
        encoder_mask:   [B, T_enc]         — 1=real, 0=pad
        cached_kv:      cached K,V from encoder (for inference, computed once)
        """
        B, T, _ = x.shape
        q = self.q_proj(x).view(B, T, self.n_heads, self.head_dim).transpose(1, 2)

        if cached_kv is not None:
            k, v = cached_kv
        else:
            T_enc = encoder_output.shape[1]
            k = self.k_proj(encoder_output).view(B, T_enc, self.n_heads, self.head_dim).transpose(1, 2)
            v = self.v_proj(encoder_output).view(B, T_enc, self.n_heads, self.head_dim).transpose(1, 2)

        new_kv = (k, v) if use_cache else None

        # Cross-attention mask from encoder padding
        attn_mask = None
        if encoder_mask is not None:
            attn_mask = (encoder_mask == 0).unsqueeze(1).unsqueeze(2)  # [B, 1, 1, T_enc]
            attn_mask = attn_mask.float() * torch.finfo(q.dtype).min

        attn_out = F.scaled_dot_product_attention(
            q, k, v,
            attn_mask=attn_mask,
            dropout_p=self.resid_dropout.p if self.training else 0.0,
            is_causal=False,  # Cross-attention is NOT causal
        )
        attn_out = attn_out.transpose(1, 2).contiguous().view(B, -1, self.d_model)
        return self.resid_dropout(self.o_proj(attn_out)), new_kv


class DecoderBlock(nn.Module):
    """
    Decoder block: self-attention → cross-attention → FFN
    Three sub-layers per block (vs two in encoder/decoder-only).
    """
    def __init__(self, dec_d_model: int, enc_d_model: int, n_heads: int,
                 d_ff: int, dropout: float, max_len: int,
                 tokens_per_frame: int = 1):
        super().__init__()
        self.self_attn_norm = RMSNorm(dec_d_model)
        self.self_attention = DecoderSelfAttention(dec_d_model, n_heads, dropout, max_len,
                                                   tokens_per_frame)

        self.cross_attn_norm = RMSNorm(dec_d_model)
        self.cross_attention = CrossAttention(dec_d_model, enc_d_model, n_heads, dropout)

        self.ffn_norm = RMSNorm(dec_d_model)
        self.ffn = SwiGLUFFN(dec_d_model, d_ff, dropout)

    def forward(self, x, encoder_output, encoder_mask=None,
                past_self_kv=None, past_cross_kv=None, use_cache=False,
                position_ids=None):
        # 1. Causal self-attention (with optional frame-level positions)
        h = self.self_attn_norm(x)
        attn_out, new_self_kv = self.self_attention(h, past_self_kv, use_cache, position_ids)
        x = x + attn_out

        # 2. Cross-attention to encoder
        h = self.cross_attn_norm(x)
        cross_out, new_cross_kv = self.cross_attention(
            h, encoder_output, encoder_mask, past_cross_kv, use_cache)
        x = x + cross_out

        # 3. FFN
        x = x + self.ffn(self.ffn_norm(x))

        return x, new_self_kv, new_cross_kv


class AudioDecoder(nn.Module):
    """
    Causal Transformer decoder for audio generation.
    At each layer, cross-attends to encoder output.
    """
    def __init__(self, vocab_size=DECODER_VOCAB_SIZE, d_model=DEC_D_MODEL,
                 enc_d_model=ENC_D_MODEL, n_heads=DEC_N_HEADS,
                 n_layers=DEC_N_LAYERS, d_ff=DEC_D_FF,
                 max_len=MAX_AUDIO_LEN, dropout=DROPOUT,
                 tokens_per_frame=1):
        super().__init__()
        self.config_d_model = d_model
        self.tokens_per_frame = tokens_per_frame
        self.token_embedding = nn.Embedding(vocab_size, d_model)
        self.embed_dropout = nn.Dropout(dropout)

        self.layers = nn.ModuleList([
            DecoderBlock(d_model, enc_d_model, n_heads, d_ff, dropout, max_len,
                         tokens_per_frame)
            for _ in range(n_layers)
        ])
        self.final_norm = RMSNorm(d_model)

        # LM head — tied with token embedding
        self.lm_head = None  # tied

    def forward(self, input_ids, encoder_output, encoder_mask=None,
                labels=None, past_key_values=None, use_cache=False):
        """
        input_ids:      [B, T_dec]
        encoder_output: [B, T_enc, enc_d_model]
        encoder_mask:   [B, T_enc]
        labels:         [B, T_dec]
        """
        h = self.embed_dropout(self.token_embedding(input_ids))

        # Build frame-level position IDs if tokens_per_frame > 1
        # [0,0,0,0, 1,1,1,1, 2,2,2,2, ...] — 4 codebook tokens share one position
        position_ids = None
        if self.tokens_per_frame > 1 and past_key_values is None:
            T = input_ids.shape[1]
            position_ids = torch.arange(T, device=input_ids.device) // self.tokens_per_frame
        # Note: when use_cache=True and past_kv exists (autoregressive step),
        # position_ids=None falls through to offset-based RoPE in attention

        new_kvs = [] if use_cache else None
        for i, layer in enumerate(self.layers):
            past_self_kv = past_key_values[i][0] if past_key_values else None
            past_cross_kv = past_key_values[i][1] if past_key_values else None

            if self.training and not use_cache:
                h, self_kv, cross_kv = torch.utils.checkpoint.checkpoint(
                    layer, h, encoder_output, encoder_mask,
                    past_self_kv, past_cross_kv, use_cache,
                    position_ids,
                    use_reentrant=False)
            else:
                h, self_kv, cross_kv = layer(
                    h, encoder_output, encoder_mask,
                    past_self_kv, past_cross_kv, use_cache,
                    position_ids)

            if use_cache:
                new_kvs.append((self_kv, cross_kv))

        h = self.final_norm(h)

        # Tied embeddings
        logits = F.linear(h, self.token_embedding.weight)

        result = {"logits": logits}
        if use_cache:
            result["past_key_values"] = new_kvs

        if labels is not None:
            shift_logits = logits[:, :-1, :].contiguous()
            shift_labels = labels[:, 1:].contiguous()
            loss = F.cross_entropy(
                shift_logits.view(-1, shift_logits.size(-1)),
                shift_labels.view(-1),
                ignore_index=-100,
            )
            result["loss"] = loss

        return result


# ── Full Encoder-Decoder Model ─────────────────────────────────

@dataclass
class V5Config:
    # Encoder
    enc_vocab_size: int = ENCODER_VOCAB_SIZE
    enc_d_model: int = ENC_D_MODEL
    enc_n_heads: int = ENC_N_HEADS
    enc_n_layers: int = ENC_N_LAYERS
    enc_d_ff: int = ENC_D_FF
    max_text_len: int = MAX_TEXT_LEN
    # Decoder
    dec_vocab_size: int = DECODER_VOCAB_SIZE
    dec_d_model: int = DEC_D_MODEL
    dec_n_heads: int = DEC_N_HEADS
    dec_n_layers: int = DEC_N_LAYERS
    dec_d_ff: int = DEC_D_FF
    max_audio_len: int = MAX_AUDIO_LEN
    # Shared
    dropout: float = DROPOUT
    ctc_weight: float = CTC_WEIGHT
    tokens_per_frame: int = 1  # 1=standard (backward compat), 4=frame-level positions


class TTSEncoderDecoder(nn.Module):
    """
    V5 Encoder-Decoder TTS Model.
    
    Forward flow:
    1. Text → Encoder → contextualized text representations
    2. Encoder output projected (enc_dim → dec_dim) for cross-attention
    3. Audio tokens → Decoder (with cross-attention to encoder) → next-token logits
    4. CTC auxiliary loss on encoder output for alignment guidance
    """
    def __init__(self, config: V5Config):
        super().__init__()
        self.config = config

        # Text encoder (bidirectional)
        self.encoder = TextEncoder(
            vocab_size=config.enc_vocab_size,
            d_model=config.enc_d_model,
            n_heads=config.enc_n_heads,
            n_layers=config.enc_n_layers,
            d_ff=config.enc_d_ff,
            max_len=config.max_text_len,
            dropout=config.dropout,
        )

        # Projection: enc_d_model → dec_d_model (if different)
        if config.enc_d_model != config.dec_d_model:
            self.enc_projection = nn.Linear(config.enc_d_model, config.dec_d_model, bias=False)
        else:
            self.enc_projection = nn.Identity()

        # Audio decoder (causal with cross-attention)
        self.decoder = AudioDecoder(
            vocab_size=config.dec_vocab_size,
            d_model=config.dec_d_model,
            enc_d_model=config.dec_d_model,  # After projection
            n_heads=config.dec_n_heads,
            n_layers=config.dec_n_layers,
            d_ff=config.dec_d_ff,
            max_len=config.max_audio_len,
            dropout=config.dropout,
            tokens_per_frame=config.tokens_per_frame,
        )

        # CTC head: predicts audio tokens from encoder output
        # This guides the encoder to learn monotonic alignment
        if config.ctc_weight > 0:
            self.ctc_head = nn.Linear(config.enc_d_model, config.dec_vocab_size)
            self.ctc_weight = config.ctc_weight
        else:
            self.ctc_head = None
            self.ctc_weight = 0.0

        self.apply(self._init_weights)

    def _init_weights(self, module):
        if isinstance(module, nn.Linear):
            nn.init.normal_(module.weight, mean=0.0, std=0.02)
            if module.bias is not None:
                nn.init.zeros_(module.bias)
        elif isinstance(module, nn.Embedding):
            nn.init.normal_(module.weight, mean=0.0, std=0.02)

    def get_num_params(self) -> int:
        return sum(p.numel() for p in self.parameters())

    def encode(self, enc_ids, enc_mask=None):
        """Run encoder + projection. Returns [B, T_enc, dec_d_model]."""
        enc_out = self.encoder(enc_ids, enc_mask)  # [B, T_enc, enc_d]
        return self.enc_projection(enc_out)         # [B, T_enc, dec_d]

    def forward(self, enc_ids, dec_ids, enc_mask=None, dec_labels=None,
                ctc_target=None, ctc_target_lengths=None):
        """
        Full forward: encoder → decoder → loss.

        Args:
            enc_ids:             [B, T_enc] — text token IDs
            dec_ids:             [B, T_dec] — audio token IDs (decoder input)
            enc_mask:            [B, T_enc] — 1=real, 0=pad
            dec_labels:          [B, T_dec] — decoder labels (-100 for masked)
            ctc_target:          [B, T_target] — target sequence for CTC (audio tokens)
            ctc_target_lengths:  [B] — lengths of CTC targets
        """
        # 1. Encode text
        enc_raw = self.encoder(enc_ids, enc_mask)     # [B, T_enc, enc_d]
        enc_proj = self.enc_projection(enc_raw)        # [B, T_enc, dec_d]

        # 2. Decode audio with cross-attention
        dec_out = self.decoder(dec_ids, enc_proj, enc_mask, labels=dec_labels)

        result = {"logits": dec_out["logits"]}
        total_loss = dec_out.get("loss", None)

        # 3. CTC auxiliary loss
        if self.ctc_head is not None and total_loss is not None and ctc_target is not None:
            ctc_logits = self.ctc_head(enc_raw)  # [B, T_enc, vocab]
            ctc_log_probs = F.log_softmax(ctc_logits, dim=-1).transpose(0, 1)  # [T_enc, B, vocab]

            # Input lengths = actual text lengths (from enc_mask)
            if enc_mask is not None:
                input_lengths = enc_mask.sum(dim=1).long()
            else:
                input_lengths = torch.full((enc_ids.shape[0],), enc_ids.shape[1],
                                          dtype=torch.long, device=enc_ids.device)

            ctc_loss = F.ctc_loss(
                ctc_log_probs, ctc_target, input_lengths, ctc_target_lengths,
                blank=PAD_TOKEN_ID, zero_infinity=True,
            )

            result["ctc_loss"] = ctc_loss
            total_loss = total_loss + self.ctc_weight * ctc_loss

        if total_loss is not None:
            result["loss"] = total_loss
        if "loss" in dec_out:
            result["ce_loss"] = dec_out["loss"]

        return result


# ── Factory functions ──────────────────────────────────────────

def create_model(device="cuda", dropout_override=None, tokens_per_frame=1) -> TTSEncoderDecoder:
    """Create V5 encoder-decoder TTS model."""
    kwargs = {}
    if dropout_override is not None:
        kwargs["dropout"] = dropout_override
    if tokens_per_frame != 1:
        kwargs["tokens_per_frame"] = tokens_per_frame
    config = V5Config(**kwargs)
    model = TTSEncoderDecoder(config)

    n = model.get_num_params()
    enc_n = sum(p.numel() for p in model.encoder.parameters())
    dec_n = sum(p.numel() for p in model.decoder.parameters())

    print(f"🏗️  V5 Encoder-Decoder TTS!")
    print(f"   Total params:  {n:,} ({n/1e6:.1f}M)")
    print(f"   Encoder:       {enc_n:,} ({enc_n/1e6:.1f}M)")
    print(f"   Decoder:       {dec_n:,} ({dec_n/1e6:.1f}M)")
    print(f"   Enc: d={config.enc_d_model}, h={config.enc_n_heads}, "
          f"L={config.enc_n_layers}, ff={config.enc_d_ff}")
    print(f"   Dec: d={config.dec_d_model}, h={config.dec_n_heads}, "
          f"L={config.dec_n_layers}, ff={config.dec_d_ff}")
    print(f"   CTC weight: {config.ctc_weight}")
    print(f"   Dropout: {config.dropout}")
    if config.tokens_per_frame > 1:
        print(f"   🎥 Frame-level positions: {config.tokens_per_frame} tokens/frame")

    model = model.to(device)
    return model


def save_checkpoint(model, optimizer, scheduler, step, loss, path):
    """Save full training checkpoint."""
    os.makedirs(path, exist_ok=True)
    model_to_save = model._orig_mod if hasattr(model, "_orig_mod") else model

    torch.save({
        "model_state_dict": model_to_save.state_dict(),
        "optimizer_state_dict": optimizer.state_dict(),
        "scheduler_state_dict": scheduler.state_dict() if scheduler else None,
        "step": step,
        "loss": loss,
        "config": {
            "enc_vocab_size": model_to_save.config.enc_vocab_size,
            "enc_d_model": model_to_save.config.enc_d_model,
            "enc_n_heads": model_to_save.config.enc_n_heads,
            "enc_n_layers": model_to_save.config.enc_n_layers,
            "enc_d_ff": model_to_save.config.enc_d_ff,
            "max_text_len": model_to_save.config.max_text_len,
            "dec_vocab_size": model_to_save.config.dec_vocab_size,
            "dec_d_model": model_to_save.config.dec_d_model,
            "dec_n_heads": model_to_save.config.dec_n_heads,
            "dec_n_layers": model_to_save.config.dec_n_layers,
            "dec_d_ff": model_to_save.config.dec_d_ff,
            "max_audio_len": model_to_save.config.max_audio_len,
            "dropout": model_to_save.config.dropout,
            "ctc_weight": model_to_save.config.ctc_weight,
            "tokens_per_frame": model_to_save.config.tokens_per_frame,
        },
    }, f"{path}/checkpoint.pt")
    print(f"💾 Saved: {path} (step {step}, loss {loss:.4f})")


def load_for_inference(checkpoint_path: str, device="cuda") -> TTSEncoderDecoder:
    """Load model from checkpoint for inference."""
    ckpt_file = os.path.join(checkpoint_path, "checkpoint.pt")
    print(f"🔧 Loading from {ckpt_file}...")
    ckpt = torch.load(ckpt_file, map_location=device, weights_only=False)

    cfg = ckpt["config"]
    config = V5Config(
        enc_vocab_size=cfg["enc_vocab_size"],
        enc_d_model=cfg["enc_d_model"],
        enc_n_heads=cfg["enc_n_heads"],
        enc_n_layers=cfg["enc_n_layers"],
        enc_d_ff=cfg["enc_d_ff"],
        max_text_len=cfg["max_text_len"],
        dec_vocab_size=cfg["dec_vocab_size"],
        dec_d_model=cfg["dec_d_model"],
        dec_n_heads=cfg["dec_n_heads"],
        dec_n_layers=cfg["dec_n_layers"],
        dec_d_ff=cfg["dec_d_ff"],
        max_audio_len=cfg["max_audio_len"],
        dropout=cfg["dropout"],
        ctc_weight=cfg.get("ctc_weight", 0.0),
        tokens_per_frame=cfg.get("tokens_per_frame", 1),
    )
    model = TTSEncoderDecoder(config)
    model.load_state_dict(ckpt["model_state_dict"])
    model = model.to(device).eval()

    n = model.get_num_params()
    print(f"✅ Loaded! {n/1e6:.1f}M params, step {ckpt['step']}, loss {ckpt['loss']:.4f}")
    return model
