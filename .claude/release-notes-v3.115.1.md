# InferHub v3.115.1: a recipe installed over weights already on disk is offered

Found by checking the published 3.115.0 `-diffusion` image. Remove a recipe the node wrote in 3.115.0 and install the
same link again, and the weights are already on disk. The worker's pull then answered `already-present` and finished,
but the recipe was never declared, so it was not offered until the worker restarted. Only a fresh download declared.

The pull now declares on that path as well. `RecipeWorkerTests.APullOfARecipeWhoseWeightsAreAlreadyHereDeclaresIt`
fails without the fix.

Checked on the GPU with the published 3.115.0 node and this worker. Dreamshaper 8's recipe was removed and the link
installed again over its cached weights. The recipe was declared, and a 512×512 picture came back.

No configuration change. Everything in 3.115.0's notes holds.
