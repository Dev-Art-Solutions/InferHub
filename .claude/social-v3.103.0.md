# InferHub v3.103.0 — social copy (unposted; Iliya posts by hand)

Blog: https://blog.devart.solutions/blog/inferhub-3-103-a-long-answer-is-a-job-you-can-follow
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.103.0
No image: the honest picture is a status changing over 40 seconds, and a screenshot of one moment of it says little.

## Facebook

Some answers from a model take minutes: a big model, a model split across several machines, a busy fleet. Holding one connection open that long is fragile, and while you wait you cannot tell a model that is still loading from a machine that has stopped.

InferHub 3.103 lets you send such a request as a job. You get an id at once. Then you follow the job: the coordinator says where it sent it and how much of the answer has come back, and the machine running it reports on it every two seconds. If the model is split across machines, the status shows how far each machine's part has loaded.

We tested it on the published images with a model split across three machines. We stopped one machine and sent a job in the same second. A normal chat gets "try again later" at that moment. The job waited 40 seconds, showed why, and then answered.

The Python, TypeScript and Go clients can use jobs today. The C# client follows when our NuGet key is renewed.

Limits we know: jobs live in memory, so a restart of the coordinator loses them. We tested with containers on one PC and an 8B model.

https://blog.devart.solutions/blog/inferhub-3-103-a-long-answer-is-a-job-you-can-follow

## X

InferHub 3.103: a long answer is now a job you can follow.

Send a chat to /api/jobs, get an id at once. The coordinator and the machine running it both update the status. For a model split across machines you see each part load.

We stopped one machine mid-test. A chat gets "try later". The job waited 40 s and answered.

https://blog.devart.solutions/blog/inferhub-3-103-a-long-answer-is-a-job-you-can-follow
