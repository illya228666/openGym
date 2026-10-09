# Run from any directory: powershell -ExecutionPolicy Bypass -File scripts/dev.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
$portableNode = Join-Path $env:LOCALAPPDATA 'Programs/openGym-Node22/node-v22.23.2-win-x64/node.exe'
$node = if (Test-Path $portableNode) { $portableNode } else { (Get-Command node.exe -ErrorAction Stop).Source }
if ((& $node --version) -notmatch '^v22\.') {
    throw 'Use Node.js 22. Reopen PowerShell after installing Node.js.'
}
if (!(Test-Path '.env.local')) { throw 'Missing .env.local; see docs/LOCAL_WINDOWS.md.' }
if (!(Test-Path 'frontend/node_modules/vite/bin/vite.js') -or !(Test-Path 'api/node_modules')) {
    throw 'Install dependencies first: npm.cmd ci --prefix frontend --ignore-scripts; npm.cmd ci --prefix api --omit=optional'
}
foreach ($port in @(3000, 5173)) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) {
        throw "Port $port is in use. Stop the previous openGym session first."
    }
}
New-Item -ItemType Directory -Force '.local' | Out-Null
$apiProcess = $null
$webProcess = $null
try {
    $apiProcess = Start-Process -FilePath $node -ArgumentList '--watch','--env-file=.env.local','api/server.js' -WorkingDirectory $repo -WindowStyle Hidden -PassThru -RedirectStandardOutput "$repo/.local/api.log" -RedirectStandardError "$repo/.local/api-error.log"
    $webProcess = Start-Process -FilePath $node -ArgumentList '--env-file=../.env.local','node_modules/vite/bin/vite.js','--host','localhost','--port','5173','--strictPort' -WorkingDirectory "$repo/frontend" -WindowStyle Hidden -PassThru -RedirectStandardOutput "$repo/.local/web.log" -RedirectStandardError "$repo/.local/web-error.log"
    Write-Host 'openGym: http://localhost:5173  |  API: http://localhost:3000/api/health'
    Write-Host 'Hot reload is enabled. Logs: .local/. Press Ctrl+C to stop both servers.'
    while (!$apiProcess.HasExited -and !$webProcess.HasExited) { Start-Sleep -Seconds 1 }
    throw 'A server stopped. Check .local/*error.log.'
} finally {
    # Only the process trees created by this invocation are stopped.
    foreach ($child in @($webProcess, $apiProcess)) {
        if ($null -ne $child -and !$child.HasExited) { & taskkill.exe /PID $child.Id /T /F | Out-Null }
    }
}
