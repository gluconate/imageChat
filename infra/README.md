# Serverless Azure deployment

## Current environment

- Subscription: Visual Studio Enterprise Subscription (`d649f686-d4b3-40b0-aea5-2dc539ceea84`).
- Tenant: IBM (`ab380dd5-7747-4247-ac62-f8e359c15458`).
- Resource group: `rg-imagechat-serverless`, East US.
- Website: https://imagechat.lemonglacier-70bb8f6c.eastus.azurecontainerapps.io/login
- Storage: `azst63py4jpyygbgg`.
- Registry: `azcr63py4jpyygbgg`; deployed image tag: `serverless-20260906-1`.
- Key Vault: `azkv63py4jpyygbgg`.

There are no VMs, managed disks, Azure Files mounts, or Caddy containers in this deployment. The root Dockerfile and compose.yaml remain for optional local use.

## First administrator

The cloud account store starts empty. Existing local accounts, cookie keys and chat history are not automatically migrated. An operator with Storage Table Data Contributor access must create the first administrator from an interactive terminal:

```powershell
powershell -NoProfile -File scripts/cloud-admin.ps1
```

Enter the username and password directly in that terminal. Never put passwords in chat, command arguments, deployment parameters or logs. The command uses the workspace-local Azure CLI sign-in and .NET SDK when installed; it does not run the local web server. It refuses to bootstrap when accounts already exist. No server restart is required after cloud bootstrap.

Use `-Check` to validate the selected CLI tenant and display the storage target without creating an account. If authentication has expired, sign into Azure CLI with the IBM tenant first. Account validation is live against Table Storage, so administrative changes revoke sessions across replicas.

## Architecture and behavior

- Container Apps Consumption web app: 0.25 vCPU, 0.5 GiB, minimum zero and maximum one replica. Azure provides HTTPS and cold-start activation.
- Blob Storage: private images, job inputs/results and shared cookie-protection keys. Cookie keys are wrapped using a Key Vault RSA key.
- Table Storage: accounts, chats, messages, request telemetry and durable work records. Conditional updates and transactions coordinate overlapping processes.
- Queue Storage: opaque generation notifications. The API returns HTTP 202 and the browser polls saved chat state.
- Event-triggered Container Apps job `imagechat-worker`: runs generation outside the HTTP request, up to three job executions in parallel.
- Scheduled job `imagechat-dispatch`: every five minutes, recovers work that was committed but not delivered to the queue and reconciles expired work.
- Existing Foundry configuration is mounted from Key Vault secret `foundryconfig`. It is not included in the image or infrastructure outputs.

An interrupted upstream call cannot be assumed safe to repeat. Recovery avoids automatically repeating a potentially billable generation after the worker has started it; inspect saved results before manually retrying. Model access and actual image generation require a signed-in account and are not verified by `/healthz`.

HTTP probes explicitly send the public Host header so ASP.NET host filtering accepts them. Do not weaken AllowedHosts to fix probe failures.

## Costs and operations

The web app scales to zero, but this is not a zero-cost deployment. The Basic container registry has a fixed charge; storage capacity/transactions, Key Vault operations, job executions, image builds and network transfer can also incur charges. The five-minute recovery job runs even when no users visit. Foundry image-generation charges are separate. Consumption free allowances are shared across the subscription and are not a spending cap.

No paid Log Analytics workspace is provisioned. Use live Container Apps console/system logs and job execution status when troubleshooting. Set a subscription budget and review actual spending after a representative usage period. Blob soft deletion retains deleted bytes for seven days; it is not a full backup of Table Storage. Establish a backup/export and retention policy before storing irreplaceable data.

## Redeployment

Use `main.bicep` for shared resources and `app.bicep` for the web app and jobs. Keep the same resource group and environment name to retain stable resource names. Always run `az deployment group what-if` before applying a template. Bicep may emit BCP081 warnings for recent API versions; Azure-side validation is also required.

Build a new uniquely tagged image with ACR Tasks using the repository Dockerfile, then deploy `app.bicep` with these parameters and the new `imageTag`:

```text
environmentName=azenv63py4jpyygbgg
identityName=azid63py4jpyygbgg
storageName=azst63py4jpyygbgg
vaultName=azkv63py4jpyygbgg
registryName=azcr63py4jpyygbgg
```

Do not print secret values during configuration updates. A Key Vault secret update may require a web revision restart and new job executions before all processes use the new value. Do not delete the storage account or resource group unless intentionally deleting cloud application data.

## Validation

The test suite includes local-mode regressions and Azurite-backed cloud tests for account concurrency, ownership, queued HTTP responses, duplicate delivery and recovery. Start Azurite on its default local ports and set `IMAGECHAT_AZURITE=1` when running cloud integration tests; otherwise those tests are skipped. Real Foundry generation is deliberately excluded from automated deployment smoke tests.