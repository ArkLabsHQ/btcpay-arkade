@echo off
setlocal
set "SCRIPT_DIR=%~dp0regtest"
set "OVERRIDE_ENV=%~dp0.env.regtest"
wsl -e bash -lc "cd \"$(wslpath '%SCRIPT_DIR%')\" && if [ -f \"$(wslpath '%OVERRIDE_ENV%')\" ]; then sed -i 's/\r$//' \"$(wslpath '%OVERRIDE_ENV%')\"; fi && bash ./start-env.sh --clean"
