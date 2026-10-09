# v3.65.0 social copy (unposted)

## Facebook

InferHub 3.65: Strata reads pictures.

Every Strata size on a node now tells the hub whether it can read a picture. A picture sent to a size that can't is refused in milliseconds, instead of after a minutes-long model load that also pushed out whatever was loaded. And the hub can add the image encoder to a size that's already installed: tick "with pictures" in the console, and only the ~0.9 GB encoder is downloaded, with the model's other settings kept.

We ran it on an RTX 3090 Ti: pictures were added to the installed Coder in about two minutes, and a test picture sent through the hub was described correctly in 15 seconds.

https://blog.devart.solutions/blog/inferhub-3-65-strata-reads-pictures

## X

InferHub 3.65: Strata reads pictures. Each size says whether it can, a picture it can't read is a 400 in milliseconds (not after a minutes-long load), and the hub adds the image encoder to an installed size. Tested on a 3090 Ti.
https://blog.devart.solutions/blog/inferhub-3-65-strata-reads-pictures
