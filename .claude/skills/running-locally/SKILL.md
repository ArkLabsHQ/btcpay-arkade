---
name: running-locally
description: Use when asked to run, start, launch, smoke-test, or verify BTCPay Server with the Arkade plugin on a local machine — including standing up the regtest environment (bitcoind, arkd, Boltz, NBXplorer) or confirming a change works in the real app.
---

# Running BTCPay + Arkade Plugin Locally

Plain agent-agnostic markdown — usable by any coding agent (it is referenced from AGENTS.md; Claude Code also auto-discovers it as a skill). Every command below was executed and verified on a cold Windows machine; bash equivalents are noted where shells differ. The app is BTCPay Server (from `submodules/btcpayserver`) with the plugin side-loaded via `DEBUG_PLUGINS`; it serves at **http://localhost:14142**.

## Prerequisites

| Requirement | Verify | Install if missing |
|---|---|---|
| .NET 10 SDK | `dotnet --version` → 10.0.x | `winget install Microsoft.DotNet.SDK.10` on Windows (new shells only pick up PATH after refresh); see dotnet.microsoft.com elsewhere |
| Docker engine running | `docker info` | Start Docker Desktop |
| Node ≥ 18 | `node --version` | — |

## One-time setup

```sh
./setup.ps1   # Windows · ./setup.sh on Linux/macOS — submodules, workloads, publishes plugin, writes appsettings.dev.json
```

## Start the regtest stack (~19 containers)

```sh
node submodules/NNark/regtest/regtest.mjs start --profile boltz,delegate
# Windows shortcut: start-test-env.cmd (args pass through: `start-test-env stop`, `... clean`, `... mine 5`)
```

First run pulls images (several minutes). Do **not** create databases manually — BTCPay and NBXplorer each auto-create their own Postgres DB (the stack's Postgres is trust-auth on port 39372).

## Launch BTCPay Server

The default `Bitcoin` launch profile already matches the stack's ports (NBXplorer 32838, Postgres 39372). Launch detached so it survives the shell; logs land in repo root (gitignored pattern `btcpay-run*.log`).

bash:
```sh
(cd submodules/btcpayserver/BTCPayServer && nohup dotnet run -lp Bitcoin \
  > ../../../btcpay-run.log 2> ../../../btcpay-run.err.log &)
```

PowerShell:
```powershell
Start-Process dotnet -ArgumentList 'run','-lp','Bitcoin' `
  -WorkingDirectory 'submodules\btcpayserver\BTCPayServer' `
  -RedirectStandardOutput btcpay-run.log -RedirectStandardError btcpay-run.err.log -WindowStyle Hidden
```

First launch builds the whole BTCPay solution — allow ~5 minutes before the port answers.

## Verify

- `http://localhost:14142/login` returns 200 ("Sign in")
- `http://localhost:14142/api/v1/health` → `{"synchronized":true}`
- `btcpay-run.log` contains `Running plugin BTCPayServer.Plugins.ArkPayServer`
- First registered user becomes admin (`ALLOW-ADMIN-REGISTRATION` is on)

## Drive it

- Fund: `node submodules/NNark/regtest/regtest.mjs faucet <address> <btc> --confirm`
- Mine: `node submodules/NNark/regtest/regtest.mjs mine [n]`
- Mempool explorer http://localhost:3000 · arkd localhost:7070 (password `secret`) · Arkade web wallet http://localhost:3003

## Stop / iterate

- Stop BTCPay: kill the `dotnet` process. Restart: same `dotnet run -lp Bitcoin`.
- After plugin code changes: `dotnet publish BTCPayServer.Plugins.ArkPayServer -c Debug -o BTCPayServer.Plugins.ArkPayServer/bin/Debug/net10.0` (what setup.ps1 does), then restart BTCPay.
- Stack: `regtest.mjs stop` keeps data; `regtest.mjs clean` wipes volumes.

## Common mistakes

- `nohup: failed to run command 'dotnet'` / `dotnet: command not found` — the shell predates the SDK install; open a new shell or extend PATH (Git Bash: `export PATH="$PATH:/c/Program Files/dotnet"`).
- Polling the port too early and concluding startup failed — check `btcpay-run.err.log` for a real error first.
- Manually running `createdb` — unnecessary, BTCPay/NBXplorer self-provision.
- Expecting Lightning at checkout — the launch profile's c-lightning endpoint (30993) isn't in this stack; Arkade's Lightning flow goes through Boltz instead.
