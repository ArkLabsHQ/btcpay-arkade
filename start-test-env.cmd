@echo off
setlocal
set "SCRIPT_DIR=%~dp0submodules\NNark\regtest"
set "OVERRIDE_ENV=%~dp0submodules\NNark\.env.regtest"
wsl -e bash -c "cd \"$(wslpath '%SCRIPT_DIR%')\" && if [ -f \"$(wslpath '%OVERRIDE_ENV%')\" ]; then sed -i 's/\r$//' \"$(wslpath '%OVERRIDE_ENV%')\"; fi && sed 's/\r$//' start-env.sh | bash -s -- --clean"
