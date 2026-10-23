# v3.75.0 social copy (unposted)

## Facebook

InferHub 3.75: we held the engines to their limits, and never told them what the limits were.

A node can cap how much memory, CPU and GPU each engine uses. colibri and Strata both size themselves from what they see, and a cap held by the operating system is not something they can see. colibri planned 147.5 GB of cache on a runtime limited to 8. Strata filled the card past its 12 GB limit, got stopped, and filled it again.

Now the node tells them, in each engine's own settings, when it starts them. colibri planned 7 GB and used 7.3. Strata answered at 11.7 GB of its 12, with no stop.

Then we ran GLM-5.3-Flash, all 321 billion parameters, through the coordinator on one box, off a hard disk: "Paris" in six minutes cold, a warm answer in 25 seconds, inside a 96 GB limit.

https://blog.devart.solutions/blog/inferhub-3-75-engines-that-know-their-limits

## X

InferHub 3.75: the node now tells colibri and Strata their memory, CPU and GPU limits instead of only enforcing them, so they plan inside them. Also: colibri 2.0 with System One, Strata 0.2, and GLM-5.3-Flash (321B) through the hub on one box.

https://blog.devart.solutions/blog/inferhub-3-75-engines-that-know-their-limits
