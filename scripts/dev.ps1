param([ValidateSet('run', 'test', 'build', 'publish', 'create-admin', 'reset-admin-password')][string]$Action = 'run')
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
    'create-admin' { dotnet run --project ImageChat.csproj -c Release --no-launch-profile -- --create-admin }
    'reset-admin-password' { dotnet run --project ImageChat.csproj -c Release --no-launch-profile -- --reset-admin-password }
    'run' { dotnet run --project ImageChat.csproj --no-launch-profile }
    # Keep tests separate from the running Debug server's locked Windows apphost.
    'test' { dotnet test tests/ImageChat.Tests.csproj -c Release }
    'build' { dotnet build ImageChat.csproj }
    'publish' { dotnet publish ImageChat.csproj -c Release -o .cache/publish }
}
exit $LASTEXITCODE