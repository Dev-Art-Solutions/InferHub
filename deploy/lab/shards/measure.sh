#!/bin/sh
# One completion through a llama-server at temperature 0; prints the speed and the text (to compare with the whole model).
#   ./measure.sh [port] [tokens]     18080 = the split head (default), 18081 = the whole model
port=${1:-18080}
tokens=${2:-64}
curl -s "http://127.0.0.1:$port/completion" -H 'Content-Type: application/json' \
  -d "{\"prompt\":\"The three most important ideas in distributed computing are\",\"n_predict\":$tokens,\"temperature\":0,\"seed\":1,\"cache_prompt\":false}" \
| python -c "import json,sys; r=json.load(sys.stdin); t=r['timings']; print(f\"prompt {t['prompt_per_second']:.1f} tok/s, generate {t['predicted_per_second']:.2f} tok/s ({t['predicted_n']} tokens)\"); print(r['content'])"
