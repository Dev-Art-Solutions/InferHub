# v3.79.0 social copy (unposted)

## Facebook

InferHub 3.79: will it fit? Choose a context, get the engine.

Paste a model and say how much context you need. The node tells you which of its engines can run it, where it would sit (all on the card, the experts in RAM, split, from disk), and why, with the numbers. Then it installs the model there, with that context. There is no minimum context, because the context you need is what decides the engine.

Strata and colibri plan their own GPU placement, so the node asks them instead of guessing. On an RTX 3090 Ti, GLM-5.3-Flash (321B) on colibri with the GPU answered warm in 8 s, against 25 s on the CPU. Checked on the card, the estimates were an upper bound, usually by 0.6–1 GB.

InferHub Desktop 0.4.0 adds Check fit… to the Models tab.

https://blog.devart.solutions/blog/inferhub-3-79-will-it-fit

## X

InferHub 3.79: paste a model, choose a context, and the node says which engine can run it, where it would sit, and why. Then it installs it there. Strata and colibri plan their own GPU placement and are asked. GLM-5.3-Flash (321B) on a 3090 Ti: 8 s warm.

https://blog.devart.solutions/blog/inferhub-3-79-will-it-fit
