param(
    [string]$StorageAccount = 'azst63py4jpyygbgg',
    [string]$TenantId = 'ab380dd5-7747-4247-ac62-f8e359c15458',
    [switch]$Check
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$cli = Join-Path $repo '.cache/azure-cli/bin'
if (Test-Path (Join-Path $cli 'az.cmd')) {
    $env:PATH = "$cli;$env:PATH"
    $env:AZURE_CONFIG_DIR = Join-Path $repo '.cache/azure-config'
}
$env:AZURE_TOKEN_CREDENTIALS = 'AzureCliCredential'
$env:AZURE_TENANT_ID = $TenantId
$env:Storage__Provider = 'Azure'
$env:Storage__AccountName = $StorageAccount
Remove-Item Env:Storage__ConnectionString -ErrorAction SilentlyContinue
Remove-Item Env:Storage__ResourceSuffix -ErrorAction SilentlyContinue
$account = az account show --query '{tenantId:tenantId,id:id}' -o json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Sign into Azure CLI before creating the cloud administrator.' }
if ($account.tenantId -ne $TenantId) { throw 'Azure CLI is signed into a different tenant.' }
if ($Check) {
    Write-Output "Cloud administrator target: $StorageAccount; tenant: $TenantId"
    exit 0
}
& (Join-Path $PSScriptRoot 'dev.ps1') create-admin
exit $LASTEXITCODE