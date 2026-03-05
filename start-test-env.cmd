@echo off
setlocal
set "SCRIPT_DIR=%~dp0submodules\NNark\NArk.Tests.End2End\Infrastructure"
wsl -e bash -c "cd \"$(wslpath '%SCRIPT_DIR%')\" && sed 's/\r$//' start-env.sh | bash -s -- --clean"
