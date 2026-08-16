# Runs the MatrikelHelfer Bridge integration tests headless in the real
# Gramps runtime. Deploys the current addon + test tool to the plugin
# directory, then executes the suite against the dedicated MHBridgeTest
# tree (wiped and reseeded on every run - never use a real tree name).

$ErrorActionPreference = "Stop"

$aio = "C:\Program Files\GrampsAIO64-6.0.8\grampsd.exe"
$plugins = "$env:APPDATA\gramps\gramps60\plugins"
$repoAddon = Join-Path $PSScriptRoot "..\MatrikelHelferBridge"

New-Item -ItemType Directory -Force "$plugins\MatrikelHelferBridge" | Out-Null
New-Item -ItemType Directory -Force "$plugins\MHBridgeTestTool" | Out-Null
Copy-Item "$repoAddon\*.py" "$plugins\MatrikelHelferBridge\" -Force
Copy-Item "$PSScriptRoot\MHBridgeTestTool\*.py" "$plugins\MHBridgeTestTool\" -Force

# Gramps writes progress to stderr; don't let that abort the script.
$ErrorActionPreference = "Continue"
$output = (cmd /c "`"$aio`" -O MHBridgeTest -a tool -p name=mhbridgetests -y 2>&1") -join "`n"
$ErrorActionPreference = "Stop"
Write-Output $output

if ($output -match "MHBRIDGE-TESTS RESULT: OK") {
    Write-Output ">>> PASS"
    exit 0
} else {
    Write-Output ">>> FAIL"
    exit 1
}
