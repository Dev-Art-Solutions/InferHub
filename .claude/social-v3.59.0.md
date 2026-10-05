# v3.59.0 social copy (unposted — Iliya posts; blog is live, ID 6ac2ef6dfc3715b8b9836ece)

## Facebook

InferHub 3.59: a closed question answered with a distribution, not a sentence.

Ask a model "should this be merged?" and it answers in a sentence that always sounds sure. colibri's Brio mode answers differently: the model writes nothing, and the engine reads how likely each answer you allow is. You get every option's probability and one number for how unsure it is.

3.59 puts that on the fleet as POST /v1/brio, on the hub and on a standalone node. A real run with OLMoE on a CPU: "request changes" 0.88, "merge" 0.09, "close" 0.03. Billed for the tokens the engine read, and the document you asked about is never logged.

https://blog.devart.solutions/blog/inferhub-3-59-a-distribution-not-a-sentence

## X

InferHub 3.59: POST /v1/brio. A colibri node answers a closed question with every option's probability and an entropy, instead of a sentence that always sounds sure.

"request changes" 0.88, "merge" 0.09, "close" 0.03 — OLMoE on a CPU.

https://blog.devart.solutions/blog/inferhub-3-59-a-distribution-not-a-sentence
