# InferHub v3.5.1 — solo mode could not start in a container

A patch on v3.5.0, shipped the same day, because the feature that release was *about* was dead on
arrival in Docker.

## What was broken

`docker run -e LocalApi__Enabled=true ...` — the headline use case of solo mode — failed to start:

```
OptionsValidationException: LocalApi:Urls must be absolute http(s) URLs (got 'http://+:8080').
```

The node image sets `ENV LocalApi__Urls=http://+:8080`, because binding loopback inside a container
means nothing reaches it through `-p`. **Kestrel accepts `http://+:8080` and `http://*:8080`;
`Uri.TryCreate` does not.** The new validator checked the address with `Uri` alone, so it rejected
the image's own default with a message blaming the URL format — and there was no configuration a
user could supply to get past it, short of overriding the variable they had no reason to know about.

Running natively on `http://localhost:5081` was unaffected, which is why every test and the live
from-source run were green.

## The fix

Listen addresses are now parsed the way **Kestrel** accepts them rather than the way `System.Uri`
does, in one place (`LocalApiOptions.TryParse`), used by both the validator and the loopback check.
The wildcard host is swapped for a placeholder before parsing and reported back separately, because
"did this parse?" and "is this exposed?" are two different questions — and conflating them is
exactly how this shipped. A wildcard is still treated as the most exposed address there is, so the
v3.5.0 rule stands: a wildcard bind with no `LocalApi:ApiKeys` and no `AllowAnonymous` still refuses
to start.

`SoloModeTests.KestrelsWildcardAddressesAreValidAddresses` pins the container's exact configuration
and fails against the old check.

## How it was found, and the lesson that keeps repeating

By pulling the published image and running it — the release ritual's D7 step. The unit suite was
green, the parity suite was green, and a real solo node had answered real inference from a real
Ollama minutes earlier. None of that touched the one configuration the container actually ships.

This is the same shape as v2.5.1 and v3.0.1: **a green suite says nothing about the artefact users
install.** The irony this time is that the wildcard forms were handled correctly in the loopback
check one method away, and simply forgotten in the validator beside it.

## Upgrading

`docker pull ghcr.io/dev-art-solutions/inferhub-node:3.5.1`. Nothing else changed; if solo mode is
off, or if you run the node natively, v3.5.0 and v3.5.1 behave identically.
