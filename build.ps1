param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$msBuildCommand = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
$msBuildPath = if ($msBuildCommand) { $msBuildCommand.Source } else { $null }

if (-not $msBuildPath) {
    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswherePath) {
        $msBuildPath = & $vswherePath -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
            Select-Object -First 1
    }
}

if (-not $msBuildPath) {
    throw 'Visual Studio MSBuild was not found. Install Visual Studio Build Tools with the .NET desktop build tools workload.'
}

& $msBuildPath (Join-Path $PSScriptRoot 'War3FixFont.sln') /restore /t:Build "/p:Configuration=$Configuration" /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

Write-Host "Built: $(Join-Path $PSScriptRoot "src\War3FixFont\bin\$Configuration\War3FixFont.exe")"
