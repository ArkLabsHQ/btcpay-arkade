@echo off
setlocal
rem Wrapper around the NNark regtest CLI (cross-platform Node orchestrator,
rem no WSL required). No arguments starts the full BTCPay-relevant stack;
rem any arguments are passed through (e.g. `start-test-env stop`,
rem `start-test-env clean`, `start-test-env mine 5`).
if "%~1"=="" (
    node "%~dp0submodules\NNark\regtest\regtest.mjs" start --profile boltz,delegate
) else (
    node "%~dp0submodules\NNark\regtest\regtest.mjs" %*
)
