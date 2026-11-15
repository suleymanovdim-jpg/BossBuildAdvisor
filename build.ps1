$ErrorActionPreference = "Stop"
$projDir = $PSScriptRoot
$tmlDir = if ($env:TML_DIR) { $env:TML_DIR } else { Join-Path ${env:ProgramFiles(x86)} "Steam\steamapps\common\tModLoader" }
$tmlDotnet = Join-Path $tmlDir "dotnet\dotnet.exe"

if (-not (Test-Path $tmlDotnet)) {
    throw "tModLoader dotnet runtime not found at: $tmlDotnet. Set `$env:TML_DIR to your tModLoader install folder."
}

dotnet build (Join-Path $projDir "BossBuildAdvisor.csproj") -c Release -p:BuildMod=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Push-Location $tmlDir
try {
    & $tmlDotnet tModLoader.dll -server -build "$projDir\" -eac (Join-Path $projDir "bin\Release\net8.0\BossBuildAdvisor.dll") -define "TRACE;JETBRAINS_ANNOTATIONS;TML_2026_09;RELEASE;NET;NET8_0;NETCOREAPP" -unsafe false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
