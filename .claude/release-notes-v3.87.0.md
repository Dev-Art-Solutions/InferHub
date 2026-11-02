# InferHub v3.87.0 — a certificate per node

Phase 127. Until now every node proved itself to its coordinator with the same enrollment secret. Anyone who had it could
join as a node, and taking one machine out of the fleet meant changing the secret on every other machine. Now the
coordinator can issue each node its own certificate for a key that never leaves the node, check a fresh signature on every
connection, and revoke one node on its own, which drops it at once.

```bash
# on the coordinator
Identity__Enabled=true          # off by default
Identity__Require=true          # optional: no certificate, no connection
```

```http
POST /api/admin/identity/tokens                  {"label": "gpu-3"}  → a one-time token, shown once
GET  /api/admin/identity
POST /api/admin/identity/nodes/{nodeId}/revoke   {"reason": "…"}
```

```bash
# on the node, once
Coordinator__EnrollmentToken=ihn_…
```

## What it does

- **The coordinator is a small certificate authority for its own nodes.** An ECDSA P-256 CA is created on first use under
  `Identity:DataDirectory` (`/data/identity` in the image). Its fingerprint is on `/api/status` and in the node's log
  after enrolling, so an operator can check that a node trusts the right coordinator.
- **The node's key stays on the node.** Given a one-time token, the node makes its own key under
  `<Node:DataDirectory>/identity/`, sends only the public half, and receives a certificate for its node id, valid
  `CertificateDays` (90). The token is spent. Enrolling again supersedes the old certificate. The node renews by itself
  after two thirds of the certificate's life, and enrolls again with a new token if it was offline past the end.
- **Every connection is signed.** The hub connection and every relay request of a sealed node carry the certificate and a
  fresh signature over the node id, the time and a nonce. The coordinator accepts only a certificate it issued and has not
  revoked, a time within `ClockSkewSeconds` (300), and a nonce it has not seen. A captured set of headers fails the second
  time. A certificate cannot register as another node.
- **Revoking is immediate.** The node is disconnected at once and refused from then on. A certificate that fails is never
  rescued by a correct shared secret sent beside it.
- **`Require` closes the shared secret.** Without it, nodes without certificates still connect with the secret, so a fleet
  can move over one node at a time. With it, the secret alone is refused.
- **Visible.** `/api/status` gains `identity` (active, revoked, expired, pending tokens, refused, the CA fingerprint);
  `/metrics` gains `inferhub_node_certificates{state}`, `inferhub_node_enrollment_tokens` and
  `inferhub_node_identity_refused_total`; the audit trail (3.85) names a certified node's requests `node:<id>`. All only
  while identity is on.
- **Rule 4's ninth exception.** The directory holds the CA key and the record of what was issued. Losing it means every
  node enrolls again. It never lets a node in.

## Upgrading

Nothing changes unless you set `Identity:Enabled`. With it off the coordinator checks the shared secret exactly as in
3.86, creates no directory, and answers the identity routes with a 404 naming the key. A node without a token and without a
certificate behaves as before. `Identity:Require` without `Identity:Enabled` fails startup. No new package.

## Not mutual TLS, on purpose

The proof travels in headers, so it works through whatever proxy, tunnel or TLS terminator sits in front of the
coordinator, on every platform the node runs on. The certificates are standard X.509, so moving to TLS client
authentication later would not need nodes to enroll again.

## Not established

- **An HA pair.** Each coordinator creates its own CA, so a node enrolled with one is refused by the other.
- **A real renewal.** It is triggered at connect time after two thirds of 90 days; the live run did not wait that long.
  The renew route is covered by unit tests; the node's renewal call is not covered end to end.
- **The Windows setup asking for the token.** It does not yet; set `Coordinator__EnrollmentToken` in the service's
  environment or through the control API's settings.
- **Key protection beyond file permissions.** The node's key is a PKCS#8 file, owner-only on Linux; on Windows it inherits
  the data directory's ACL. No TPM or OS key store.

## Verification

- Suite: 2 744 passed, 63 skipped, 0 failed (Desktop 36, Shared 433, Coordinator 997, Node 731, Mesh 547). New: `NodeIdentityTests` (10, Coordinator — token spent and expired, replay, clock skew, another key,
  a self-signed lookalike, another node's id, superseded/revoked/expired named, renewal, a restart keeps the CA and the
  record, the filter under `Require` and the no-fallback rule, the scrape), `NodeIdentityMeshTests` (4, real SignalR and
  HTTP — enroll with a token and run a job; an aborted connection comes back on its own with a fresh proof; revoke drops
  the node and keeps it out; the secret alone refused under `Require`, accepted without it), `NodeCredentialTests` (3).
- Live, from source: a coordinator with `Identity:Require`, the relay and the audit trail on; sealed nodes on the real
  Ollama.
  - A node with only the shared secret was refused on every retry, naming `Identity:Require`.
  - A one-time token from `POST /api/admin/identity/tokens`; the node enrolled (its log shows the same CA fingerprint as
    the coordinator), registered, and a chat through it answered 200.
  - A model pull on that sealed node went through the relay signed with its certificate; the audit line says
    `node:3cb7447d-…` for the relay request.
  - Revoked with a reason: the node was gone from the fleet in 161 ms, and each retry (certificate and secret both) was
    refused naming the revocation and the reason. `/metrics`: one revoked certificate, 18 refusals.
