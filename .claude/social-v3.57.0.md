# v3.57.0 social copy (unposted — Iliya posts; blog is live, ID 6ac02e4cc0fb8f20160dfe7c)

## Facebook

InferHub 3.57: editing a picture on a CPU.

The 4-step lcm-dreamshaper model from 3.56 can now edit and vary a picture too, with no graphics card: about 10 seconds for a 512×512 edit at 16 CPU threads. It cannot use a mask, so a masked edit gets a clear 400 before the model even loads.

Counting its steps one by one showed something we had assumed wrong. On Stable Diffusion, a lower edit strength means fewer steps, and we bill the steps that ran. This model runs all of its steps at any strength, so an edit at 0.5 would have been billed for half the work. Now it is billed correctly, and the worker logs any model whose steps don't match the bill.

https://blog.devart.solutions/blog/inferhub-3-57-editing-on-a-cpu-and-the-steps-we-would-have-billed-wrong

## X

InferHub 3.57: the 4-step CPU image model can now edit a picture, ~10 s at 512×512.

Counting its steps showed it runs all of them at any strength, so an edit would have been billed for half the work. Fixed, and now checked on every run.

https://blog.devart.solutions/blog/inferhub-3-57-editing-on-a-cpu-and-the-steps-we-would-have-billed-wrong
