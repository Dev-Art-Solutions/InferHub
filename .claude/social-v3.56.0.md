# v3.56.0 social copy (unposted — Iliya posts; blog is live, ID 6abede5f0bb5c39555cba58a)

## Facebook

InferHub 3.56: a picture on a CPU in seconds.

The new lcm-dreamshaper model needs 4 steps instead of 30, so a node with no graphics card can now draw a 512×512 picture in about 6 to 27 seconds, depending on how many CPU threads it gets.

Running it showed something else. A model that cannot use a negative prompt ("no text in the picture") does not complain, it just ignores it. Two models in our catalogue, sdxl-turbo and flux-schnell, had been doing that since 3.16, and the caller always got a normal 200. Now such a request gets a 400 that says why and what would make it work.

https://blog.devart.solutions/blog/inferhub-3-56-a-picture-on-a-cpu-and-a-negative-prompt-nobody-heard

## X

InferHub 3.56: a 4-step image model that runs on a CPU in seconds.

It also found that a negative prompt a model can't use was silently ignored, by sdxl-turbo and flux-schnell too, since 3.16. Now that's a 400 that says why.

https://blog.devart.solutions/blog/inferhub-3-56-a-picture-on-a-cpu-and-a-negative-prompt-nobody-heard
