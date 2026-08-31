# The public sandbox

A running Remit anyone can drive from the browser without installing anything:
<https://value.al/tools/remit-console.html> talks to <https://remit-sandbox.value.al>.

What it is: the three services, PostgreSQL and RabbitMQ from `docker-compose.yml` in this
folder, on the same small host that serves value.al, behind that host's Caddy
(`Caddyfile.snippet`), with:

- **no authentication** — it is a demonstration, not a service;
- **CORS** for `value.al` only and a **rate limit of 120 requests per minute per client**
  (`EdgeHosting` in BuildingBlocks; both are configuration, off by default);
- **public webhook secrets** (`whsec_alpha_sandbox`, `whsec_beta_sandbox`) so anyone can sign a
  settlement — forging one *incorrectly* is the point of the exercise;
- a **nightly wipe** of every table (`reset.sh`), schema kept;
- migrations on boot, which the real deployment (ADR-0008) forbids and a sandbox may allow;
- no tracing backend — `Otel:Endpoint` is unset, so nothing is exported.

How it is deployed: the value.al Azure Pipeline (which runs on the host) clones this repository,
runs `docker compose -p remit-sandbox up -d --build`, keeps the Caddy block between marker
comments in the host's Caddyfile, reloads Caddy, and checks `/health/ready` on each container.
The only manual step, once: a DNS `A` record for `remit-sandbox.value.al` pointing at the host —
Caddy obtains the certificate itself after that.

By hand, on the host:

```sh
cd ~/sites/remit-sandbox
git clone --depth 1 https://github.com/value-al/Remit src   # or git -C src pull
docker compose -p remit-sandbox -f src/deploy/sandbox/docker-compose.yml up -d --build
docker exec remit-funding wget -qO- http://localhost:8080/health/ready
docker exec remit-reset sh /reset.sh                         # wipe now
```
