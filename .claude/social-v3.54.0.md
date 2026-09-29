# v3.54.0 social copy (unposted — Iliya posts)

## Facebook

InferHub 3.54: a deadline for each kind of work.

Until now, a chat answer and a five-second video clip waited on the same deadline, 300 seconds by default. A real clip needs about 8 minutes, so a hub that made video had to give every chat request half an hour as well.

Now each capability can have its own deadline: `"Deadlines": { "video": 3600 }`, and chat keeps its five minutes. Nothing changes unless you set it. A job that runs out now says which deadline it hit, and which config key set it.

The first tests that let a deadline actually expire found two older bugs, fixed in the same release. The tool and audio routes answered a timeout with an empty 500 (now a 504). The in-flight gauge on /metrics never came back down after a timed-out job. We reproduced both on the previous published image before calling them fixed.

https://blog.devart.solutions/blog/inferhub-3-54-a-deadline-for-each-kind-of-work

## X

InferHub 3.54: one dispatch deadline per capability. Video gets an hour and chat keeps 5 minutes, and a timeout names the key that set it.

The new tests also caught an empty 500 on the tool routes and an in-flight gauge that never came back down. Both are fixed.

https://blog.devart.solutions/blog/inferhub-3-54-a-deadline-for-each-kind-of-work
