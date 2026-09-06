param([ValidateSet('run', 'test', 'build', 'publish')][string]$Action = 'run')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
# Process-local only: no user/machine environment or package cache changes.
$env:DOTNET_CLI_HOME = Join-Path $repo '.cache/dotnet'
$env:NUGET_PACKAGES = Join-Path $repo '.cache/nuget'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repo '.cache/nuget-http'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:TEMP = Join-Path $repo '.cache/tmp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
switch ($Action) {
    'run' { dotnet run --project ImageChat.csproj --no-launch-profile }
    'test' { dotnet test tests/ImageChat.Tests.csproj }
    'build' { dotnet build ImageChat.csproj }
    'publish' { dotnet publish ImageChat.csproj -c Release -o .cache/publish }
}
exit $LASTEXITCODE