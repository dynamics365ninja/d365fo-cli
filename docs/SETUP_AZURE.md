# Shared deployment (Azure App Service)

Running **one** `d365fo-mcp` instance a whole team points at, instead of a stdio process per
developer. [MIGRATION_FROM_MCP.md](MIGRATION_FROM_MCP.md#http-transport--shared-deployment-azure-app-service)
explains the transport and the mode split and why the auth is an API key rather than Entra ID;
this document is the deployment itself.

> **Do you need this?** Almost certainly not. stdio needs no ports, no secrets and no hosting: the
> client starts the process. A shared instance earns its keep when the index is expensive to
> build and the people who need it do not have a D365FO VM each — a functional consultant asking
> questions about metadata, a build agent, a support rotation. If everybody already has the
> package tree on disk, stop here and use stdio.

---

## What actually gets deployed

Two files and an environment:

| | |
|---|---|
| The binary | `d365fo-mcp`, published self-contained for the App Service platform |
| The index | One SQLite file — 750 MB for a full installation, and read-only in this deployment |
| The environment | `D365FO_INDEX_DB`, `MCP_SERVER_MODE=read-only`, `API_KEY` |

Note what is **not** there: no `PackagesLocalDirectory`, no bridge, no compiler. A shared instance
serves the index and nothing else, which is exactly what `read-only` mode enforces — every tool
that needs local files or the bridge is omitted from `tools/list` and refused at call time.

---

## 1. Build the index somewhere that has the packages

On a development VM:

```sh
d365fo index build
d365fo index extract
d365fo index optimize                 # VACUUM + ANALYZE — checkpoints the WAL first
d365fo index status --output json     # names the file to copy
```

Copy the file `index status` names. Run `index optimize` first, and not only for the space: it
checkpoints the write-ahead log, so what you copy is the whole database rather than a file with
live `-wal` and `-shm` siblings you would have had to remember to copy too.

To move it compressed — a slow link, a CI cache — `index export --out snapshot.gz` writes a GZip
snapshot and `index import --from snapshot.gz` restores one. That pair wants the CLI at both ends,
so for App Service it is `export` here and `gunzip` there.

## 2. Publish the adapter

```sh
dotnet publish src/D365FO.Mcp -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -o ./publish
```

Windows App Service works the same with `-r win-x64`. The CI job `published-cli` exists because
the published shape is not the built shape — a trimmed single file has failed where an ordinary
build passed (issue #182) — so smoke the published binary before deploying it:

```sh
./publish/d365fo-mcp --legacy < probe.jsonl     # a tools/list request; see MCP_CONFIG.md
```

## 3. Configure the App Service

```sh
az webapp config appsettings set -g <rg> -n <app> --settings \
  D365FO_INDEX_DB=/home/site/data/d365fo-index.sqlite \
  MCP_SERVER_MODE=read-only \
  API_KEY=<a long random string> \
  MCP_HTTP_PORT=8080

az webapp config set -g <rg> -n <app> --startup-file "/home/site/wwwroot/d365fo-mcp --http"
```

Put the index on the App Service file share (`/home/site/data`), not in the deployment package:
it is large, it changes on a different cadence than the binary, and a deployment slot swap should
not have to move it.

`API_KEY` is not optional in practice. Unset, the server logs a startup warning and serves
`/mcp` to anyone who can reach it — every tool in the catalog, against your metadata.

## 4. Verify before handing out the URL

```sh
curl https://<app>.azurewebsites.net/health
# {"status":"ok","mode":"read-only","indexReachable":true}
```

Three things that answer must say: `ok`, the mode you set, and `indexReachable: true`. A `false`
there means the binary started and the database did not open — the usual cause is a path that
exists on the build machine and not in the container.

Then check the gate actually holds:

```sh
curl -s https://<app>.azurewebsites.net/mcp \
  -H 'content-type: application/json' -H 'X-Api-Key: <key>' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' | grep -c generate_object
# 0 — read-only mode does not advertise it
```

## 5. Point the team at it

```sh
d365fo connect https://<app>.azurewebsites.net --api-key <key>
d365fo connect https://<app>.azurewebsites.net --editor vscode --api-key <key>
```

`connect` probes `/health` before writing and merges into the existing config rather than
replacing it.

---

## The read-only + write-only pattern

A read-only shared instance answers questions. It cannot generate, modify, or write labels,
because those need the package tree and the bridge on the same machine as the caller.

Developers who need both run a **local** `d365fo-mcp` with `MCP_SERVER_MODE=write-only` alongside
the shared read-only one, and register both. The split is not cosmetic: `CliMcpParityTests` and
the adapter's own tests assert that no tool which writes is reachable in `read-only` mode, so a
new write tool that nobody classified fails the build instead of shipping into a shared
deployment. See [MIGRATION_FROM_MCP.md](MIGRATION_FROM_MCP.md#the-read-only--write-only-split-upstreams-local_tools-pattern)
for the tool-by-tool breakdown.

---

## Keeping the index fresh

The index is a mirror, and a stale mirror answers confidently with old facts. Re-extract on the
machine that has the packages and re-upload after a platform update or a deployment of your own
models:

```sh
d365fo index extract          # only re-reads models whose XML changed
d365fo index optimize         # checkpoint + vacuum, so the one file is complete
az webapp deploy -g <rg> -n <app> --type static --src-path ./d365fo-index.sqlite \
  --target-path /home/site/data/d365fo-index.sqlite
```

The adapter opens the file per request through the shared repository, so an upload replaces what
subsequent calls see. Restart the app if you want certainty rather than eventual consistency.

---

## Cost and sizing

A B1 App Service instance is enough: the work per call is a SQLite query, and the process is idle
between them. The one number that matters is disk — the index is ~750 MB for a full standard
installation plus custom models, so the file share, not the CPU, is what to size for.

---

## See also

- [MCP_CONFIG.md](MCP_CONFIG.md) — client configuration and the environment the adapter reads.
- [MCP_TOOLS.md](MCP_TOOLS.md) — which tools exist, generated from the adapter itself.
- [CONFIGURATION.md](CONFIGURATION.md) — every environment variable.
- [SECURITY.md](../SECURITY.md) — reporting a vulnerability.
