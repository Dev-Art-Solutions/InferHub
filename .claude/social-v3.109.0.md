# v3.109.0 — social copy

Blog: https://blog.devart.solutions/blog/inferhub-3-109-the-coordinator-places-a-group

## Facebook

InferHub 3.109: when one model is split across several machines, you no longer pick the machines by hand. Name the model, and the coordinator chooses a head that holds the file and the members with the shortest round trips whose memory holds the model.

A running group is not moved for a slightly better number. When a machine in it leaves, the group is placed again and says why.

In our lab (four machines in Docker, two of them 20 ms away) it chose the near machine: 64 tokens in 9.9 s. With that machine stopped, it moved to a far one within a minute: 11.8 s, the same text.

https://blog.devart.solutions/blog/inferhub-3-109-the-coordinator-places-a-group

## X

InferHub 3.109: name the model, and the coordinator picks the machines for a split model itself: a head holding the file, members with the shortest round trips.

Lab: near machine 9.9 s for 64 tokens; after it stopped, a far one 11.8 s. Same text.

https://blog.devart.solutions/blog/inferhub-3-109-the-coordinator-places-a-group
