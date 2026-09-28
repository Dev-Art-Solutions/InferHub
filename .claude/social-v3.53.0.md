# Social copy — v3.53.0 (phase 88: a 360° panorama, back as a cubemap)

Blog: https://blog.devart.solutions/blog/inferhub-3-53-a-panorama-back-as-a-cubemap
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.53.0

## X / Twitter

InferHub v3.53: ask for a 360° panorama as a cubemap and get six square faces in the OpenGL order, ready for Unity, Godot or three.js. Billed as the render it came from, and the orientation is tested against the GL table, not eyeballed.

https://blog.devart.solutions/blog/inferhub-3-53-a-panorama-back-as-a-cubemap

## Facebook / LinkedIn

**A 360° panorama, back as a cubemap.**

InferHub has rendered 360° panoramas on your own graphics card since 3.17. They come out as one wide 2:1 picture, which is what a 360° photo viewer wants and not much else. Game engines and 3D libraries want a sky as six square faces, one per side of a cube, and converting it yourself is easy to get subtly wrong: one mirrored face looks fine until you stand inside it.

v3.53 does the conversion on the node when you ask for it with one header. You get one image with the six faces in the standard OpenGL order, and you are billed for the render, not for the size of the strip.

To make sure the faces point the right way, the tests paint every pixel of a test panorama with its own direction in space, so each face pixel can be checked against the OpenGL definition. We then broke one face on purpose to make sure the test notices.

Running the same check on the published image found one more thing: the pinned image library warns that an argument we used is going away. Nothing is broken today, but a future upgrade would have quietly turned every cube back into a panorama. It is fixed, and the tests now fail on warnings like that.

Not yet checked: a real panorama cut on a card, and the result opened in a game engine.

https://blog.devart.solutions/blog/inferhub-3-53-a-panorama-back-as-a-cubemap
