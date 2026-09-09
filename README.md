# Frame / ImageChat

A local image-creation studio for **GPT-image-2, MAI-Image-2.5-Flash and FLUX.2-pro deployments in Microsoft Foundry**. A single ASP.NET Core application serves the website and a server-side API. No Node.js, frontend build, CDN, separate database server, or extra text-chat model is required.

## Azure serverless deployment

The application also supports Azure Container Apps Consumption with Blob, Table and Queue Storage. See [deployment and administrator setup](infra/README.md) for the deployed environment, scale-to-zero behavior, costs and operations. The local SQLite instructions below still apply to local mode; the serverless deployment does not use a mounted SQLite database.

## Included features

- Streaming multimodal conversation through the `gpt-5.6-sol` Responses deployment, with configurable long-history budgets.
- Text-only generation, image-only variation, and text + image editing.
- Follow-up requests using earlier successful prompts and the latest generated image.
- Per-chat model selection with independent deployment readiness and model-specific size/quality controls.
- New chats, persistent history, title search, reopening and deleting conversations.
- Parallel generations across independent chats, with per-chat drafts and live progress in history.
- Login portal, per-user chat/image ownership, administrator account management and password resets.
- Administrator telemetry: generation/edit counts and request outcomes filtered by time and client IP.
- PNG/JPEG upload, drop and paste, preview, one-click image download, quality and size controls.
- Server-only credentials; configurable API-key or Microsoft Entra authentication.
- One portable SQLite database for chats, original uploads and generated image bytes.
- Unit/API integration tests using simulated model responses (no Azure cost).
- Linux/Windows development scripts, non-root Docker image and Docker Compose.

## Run locally on Windows

Prerequisite: **.NET 10 SDK**. It is already installed on the machine where this repository was created.

1. Open **appsettings.Local.json** in the repository. This local-only file has already been created, is git-ignored, and is excluded from publishing and Docker builds.
2. Fill in these fields under `Foundry`:
   - `Endpoint`: your resource's HTTPS root from **Keys and Endpoint**, e.g. `https://YOUR-RESOURCE.openai.azure.com/`. Do **not** enter a Foundry project URL, `/api/projects/...`, or a full image-operation URL.
   - `Deployment`: the **deployment name** you assigned to GPT-image-2, which may differ from the model name.
   - `ApiKey`: your resource key. Enter it directly in the local file, **not in Copilot chat**, browser code, or a committed file.
   - `AuthMode`: `ApiKey` (default), or see keyless authentication below.
3. Create the first administrator **once**, using the interactive command below. Enter the password directly in your own terminal, never in Copilot chat. No default account or public registration is provided.

  ```powershell
  powershell -NoProfile -File scripts/dev.ps1 create-admin
  ```

4. In VS Code select **Terminal → Run Task → ImageChat: Run locally**. If the server was already running, restart it after administrator setup to claim pre-login chat history.
5. Open **http://localhost:5080/login** and sign in. Administrators can open **http://localhost:5080/admin**.

Alternatively, from the repository:

```powershell
powershell -NoProfile -File scripts/dev.ps1 run
```

The server starts without accounts or Foundry credentials, but the studio and APIs require login. The login screen explains offline administrator setup when no accounts exist. After login, a setup banner explains missing Foundry configuration; generation stays disabled until configured. **Restart the server after changing configuration.** The readiness indicator checks configuration only, not connectivity. Your first real generation verifies access and can incur your deployment's normal charges.

On a fresh clone, the ignored local file will be absent. Create it yourself with:

```json
{
  "Foundry": {
    "Endpoint": "https://YOUR-RESOURCE.openai.azure.com/",
    "Deployment": "YOUR-DEPLOYMENT-NAME",
    "AuthMode": "ApiKey",
    "ApiKey": "YOUR-KEY"
  }
}
```

Never commit the completed file. API keys are not encrypted at rest in this local configuration, so protect the repo directory with your OS user permissions.

### Linux / macOS

Install the .NET 10 SDK, create the same local configuration, then:

```sh
sh scripts/dev.sh create-admin
sh scripts/dev.sh run
```

The development scripts keep SDK/NuGet/temp caches in `.cache/` and set environment variables only for their process. They do not install anything globally or change machine/user settings. The app defaults to localhost only.

## Login and user management

The entire studio, chat/image APIs, and administrator pages require authentication. `/login`, sign-in assets, `/api/auth/session`, and `/healthz` are public; there is **no web-based first-admin creation endpoint**. Bootstrap runs only in an operator's interactive terminal and refuses to run once accounts exist. If the development server is running, the bootstrap script uses Release output to avoid its locked Windows executable.

- The operator-managed **data/users.json** file stores usernames, immutable user IDs, roles, enabled state, password hashes, session-revocation stamps and durable login lockout metadata. It is outside the web root, ignored by Git, and excluded from publication/container images with the rest of the data directory. Do not move it into the web root or commit it.
- Passwords are **salted PBKDF2-HMAC-SHA256 with 600,000 iterations**, a random 16-byte salt and a 32-byte derived hash. The versioned encoding stores algorithm, cost, salt and hash. Plain SHA-256 is **not** a safe password-storage algorithm. No plaintext password is written to the file or returned by user APIs.
- Usernames are 3–64 ASCII letters/digits/`.`/`_`/`-`, unique case-insensitively. Passwords are 12–128 characters. Roles are `admin` or `user`.
- Administrators can create/delete accounts, assign roles, enable/disable accounts and reset passwords from `/admin`. Deleting, disabling, demoting the **last enabled administrator** is rejected. Username changes are intentionally not supported.
- Role, enabled state, password resets and logout invalidate existing account sessions. **Logout signs out all sessions for that account**, including cookies copied elsewhere. An authenticated user can change their own password from Settings; they must sign in again afterward. Other tabs detect session expiry/account changes and clear private in-memory drafts.
- Cookies are HttpOnly, SameSite=Strict, eight-hour non-sliding tickets. Identity, role and revocation stamp are validated against the account file on each authenticated request. Missing/corrupt storage fails closed; it is never silently recreated after disappearance in a running process.
- Sign-in and password-change attempts share a **10-per-minute per-client-IP** limit. An existing account locks for 15 minutes after five failed logins in a 15-minute window. An administrator password reset clears lockout. Unknown usernames receive the same generic response and password-hash work, without creating records for attackers' names.
- Browser mutations require the same-origin application header and reject mismatched Origin/cross-site requests. No cross-origin API sharing is enabled. These controls complement—not replace—authentication and TLS.
- Users, including administrators, only access **their own** chats and image downloads. Telemetry grants administrators metadata access, not other users' image/prompt access. Pre-login chats are assigned to the earliest administrator at the first server startup after bootstrap; existing history is not deleted. Newly created users start with no chats.
- Deleting a user does not erase their historical telemetry or automatically delete their stored chats. Recreating the same username produces a new ID and does not grant access to the deleted account's chats. Plan administrative retention/cleanup before broad production use.

Use the UI rather than hand-editing the accounts file while running. Account writes are synchronized and atomically replaced; back up the whole data directory with the app stopped. If administrators lose credentials, an operator with host/filesystem access can use the offline reset command below. There is no public password-recovery endpoint. Do not delete the accounts file to recover a password; user IDs govern chat ownership.

### Reset an existing administrator password

From your **own interactive terminal**, in the repository:

```powershell
powershell -NoProfile -File scripts/dev.ps1 reset-admin-password
```

Or on Linux/macOS:

```sh
sh scripts/dev.sh reset-admin-password
```

Alternatively select **Terminal → Run Task → ImageChat: Reset administrator password** in VS Code. Enter the existing administrator username, the new password (12–128 characters), and confirmation. Password entry is hidden, so no characters appear as you type. The old password is not required: this is an **operator-level recovery tool**, authorized by access to the host and account file, not a public API. Do not supply passwords in command arguments, environment variables, pipes, chat, or logs.

For an already-published application, run `dotnet ImageChat.dll --reset-admin-password` in its application directory using an interactive terminal and the **same `Storage__Directory` and configuration** as the server. The command reads the existing account store, hashes the new password with a fresh salt, clears failed-login lockout, and revokes all existing sessions on their next request. User ID, role, enabled state, chats, images and telemetry are preserved. It does not create missing accounts or promote ordinary users. Disabled administrators remain disabled.

**No server restart is required** for the password change itself: the running app validates credentials from the account file. Sign in again at `/login` using the new password. Existing in-flight image requests may still complete. `create-admin` remains for first-time setup only and cannot reset an existing account. If storage is missing/corrupt, fix the configured path or restore a protected backup rather than deleting or overwriting user identities.

## Administrator telemetry

Open `/admin` as an administrator to see all-time or filtered totals for **generated**, **edited**, **legacy** and **total images**, request records, failures, rejections and pending work. Filters use an inclusive **From**, exclusive **To**, and optional **exact IPv4/IPv6 address**. The UI accepts local time and sends explicit UTC timestamps; results show the browser's timezone. Totals include the entire filtered dataset, not just the current 50-row page.

Each authenticated image-request attempt that passes the authentication/CSRF/HTTPS guards is recorded in SQLite **before** generation throttling. The record contains an opaque request ID, user ID/username, resolved client IP, UTC request/completion time, operation (`generation`, `edit`, or `unknown` before validation), outcome, output-image count and HTTP status. This includes rate-limited, invalid, missing-chat, busy-chat and upstream-failed requests. Unauthenticated traffic, sign-in attempts and requests rejected by the earlier security guards are **not** part of this image telemetry.

- Text follow-ups that reuse an existing image count as **edits**, as do image-only and text+image submissions. Inputs themselves are not counted as generated outputs. Only successfully saved outputs increment image totals; the successful assistant message and telemetry count are committed in one transaction.
- A pending record is visible while generation runs. Server restarts mark unfinished request records **interrupted**, not successful. A request can still have incurred upstream charges even if its response was lost, so these are application-output counts—not billing measurements.
- Existing successful stored outputs are backfilled once as **legacy / unknown**, with their existing message timestamp and **no invented IP, username or generation/edit classification**. Images deleted before telemetry was introduced cannot be reconstructed. Exact-IP filtering excludes legacy records with unknown IP.
- Deleting chats does **not** delete telemetry or reduce historical output totals. Records are independent of chat/message foreign keys and contain **no prompts, uploaded/generated image bytes, passwords, keys or cookies**.
- IP addresses and usernames can be personal data. The studio discloses collection, but the operator remains responsible for notices, access controls, lawful use, retention periods and deletion procedures. Records currently persist until an operator applies an appropriate retention policy; no automatic expiry/export system is included.

### Accurate IPs behind Azure / reverse proxies

By default forwarding is **disabled** and the app records the connection's remote IP, ignoring client-supplied `X-Forwarded-For`. In local testing this is normally loopback; behind an unconfigured cloud ingress it will be the proxy, not the end user's address.

For an actual trusted ingress, configure:

- `ReverseProxy__Enabled=true`
- `ReverseProxy__KnownProxies__0=<actual proxy IP>` and/or `ReverseProxy__KnownNetworks__0=<actual trusted proxy CIDR>`
- `Security__RequireHttps=true` and HTTPS at the ingress
- `AllowedHosts=<actual public hostname>`

The app processes **one trusted forwarding hop**, accepts only `X-Forwarded-For` and `X-Forwarded-Proto` from the allowlist, normalizes IPv4-mapped IPv6, and rejects empty allowlists and `/0` trust-all networks when enabled. Do not guess Azure proxy ranges or trust every address/private network. Restrict direct container access to the trusted ingress; multiple proxies require a topology-specific configuration change and spoofing tests. The generic `ASPNETCORE_FORWARDEDHEADERS_ENABLED` convenience setting is disabled by the application to avoid implicit trust-all behavior.

IP identifies a network source, not necessarily a person (NAT/VPNs/shared networks apply). The authenticated user ID provides account attribution. Reference: [ASP.NET Core reverse proxy guidance](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

## Chat with GPT-5.6-sol (text and images)

Select **Chat** in the composer to use `gpt-5.6-sol`, independently of the three image-generation models. Chat can answer text prompts, analyze an uploaded PNG/JPEG, and discuss prior images in the same conversation. Image-only messages use “Describe this image and help me understand it.” Replies are real model text, rendered as plain text (not executable HTML). Switch back to **Images** to generate or edit pictures.

Merge the `Foundry.Chat` section from [appsettings.Models.example.json](appsettings.Models.example.json) into your ignored local configuration, preserving existing settings. Configure its own API key or `DefaultAzureCredential`, then restart. The supplied endpoint is `https://embedding-0483-resource.services.ai.azure.com/openai/v1/responses`; an HTTPS resource root is also accepted and the app appends `/openai/v1/responses`. No API-version query or another provider's credentials are inherited. Deployment names are configurable—`gpt-5.6-sol` is treated as your deployment identifier, not a verified public model capability specification.

Requests use the Responses API with `stream: true`, `store: false`, `max_output_tokens`, and chronological user/assistant messages. Images are sent as `input_image` base64 data URLs alongside `input_text`. Only visible output text/refusal events are forwarded; reasoning/tool-event content is not exposed. No tools, browsing, image-generation tools or external image URL fetching are enabled. Set `Foundry:Chat:Streaming=false` if your deployment doesn't support streaming; a complete JSON response is handled in that mode. An unsupported request is never automatically retried with different parameters or another model.

### Longer context, with explicit application budgets

| Setting (`Foundry:Chat:` prefix) | Default | Meaning |
| --- | --- | --- |
| `MaxPromptCharacters` | 64,000 | Maximum prompt length; configurable up to 256,000 |
| `MaxContextCharacters` | 256,000 | Text across retained input messages; configurable up to 1,000,000, at least the prompt budget |
| `MaxContextMessages` | 200 | Retained input-message budget; 2–1,000 |
| `MaxContextImages` | 8 | Current and historical images; 1–16 |
| `MaxContextImageBytes` | 33,554,432 | Total decoded image bytes; configurable 20–64 MiB |
| `SupportsImages` | `true` | Operator can disable image input if the deployment rejects it |
| `MaxOutputTokens` | 2,048 | Requested output-token limit; separate from input context |
| `TimeoutSeconds` | 180 | Provider response timeout; 30–210 seconds |

These are **application character/message/image budgets, not the model's token context window**. The exact token window for this deployment has not been verified. Image token accounting also depends on the model. Adjust budgets to your deployment's supported limits; Foundry may reject context that exceeds them. Each upload remains limited to one PNG/JPEG of 20 MiB; historical images can bring the total up to the configured history limit.

The current message and image take priority. The app retains recent **whole successful turns**, excludes failed/pending turns, and includes previous chat answers. Prior generated images are user reference inputs, not fabricated assistant answers. Selected image bytes are loaded only through owner-authorized image IDs. If older text/images are omitted for a budget, the stream explicitly flags **context truncated** and the UI shows a notice. Saved history is not deleted. Replies currently have a separate 12,000-character application limit for safe cloud-table storage; a large input window does not imply unlimited output.

### Streaming, persistence and cloud behavior

`POST /api/chats/{id}/text-messages` accepts multipart prompt/model/image or JSON prompt/model (text-only), and emits SSE `started`, `delta`, `completed` or `failed` events. The browser receives a compact, explicitly partial snapshot of the current turn, while GET chat history always returns stored messages. This keeps long-history data out of individual SSE events. UTF-8/chunk boundaries are handled incrementally. Drafts and streaming replies remain scoped to each chat; other chats remain usable.

Partial reply snapshots are saved approximately every 500 ms when new text arrives. Final canonical text replaces deltas; interrupted streams keep a failed partial rather than reporting success. A disconnected browser does not trigger a retry; the server continues within its timeout and saves the outcome. Reopen the chat to recover. A process crash may lose text since the last snapshot. Local startup marks pending turns failed; Azure uses a fixed five-minute text deadline and guarded recovery on reads/dispatcher passes. No recovery repeats the billable call.

Text chat currently streams **directly from the web process**, including in Azure mode; existing image jobs continue through their durable queue. The default 180-second provider budget is below Container Apps' documented 240-second HTTP timeout, but proxy/network limits can still interrupt a stream. Long cloud prompts are stored in private blobs to avoid Azure Table property limits. Keep cookie/auth, ingress and storage protections enabled.

Text requests have a separate **20-per-minute per-account** app limit (shared across Azure replicas). They appear in admin telemetry as operation `chat`, with user/IP/timestamps/model/outcome, but always count **zero generated images**. Telemetry does not contain prompt/reply text or image bytes. No live model requests are made by tests.

All settings support environment overrides such as `Foundry__Chat__ApiKey`, `Foundry__Chat__MaxContextCharacters`, and `Foundry__Chat__Streaming`. Compose supports `CHAT_ENDPOINT`, `CHAT_DEPLOYMENT`, `CHAT_AUTH_MODE`, `CHAT_API_KEY`, and `CHAT_STREAMING`. Reference: [Azure Responses API](https://learn.microsoft.com/azure/ai-foundry/openai/how-to/responses).

## How image conversations work

**These are image-output models, not general text-chat models.** Their Images APIs do not have native chat sessions. This app provides conversation memory:

1. A first text-only message calls `images/generations`.
2. A message with an uploaded image calls `images/edits` with multipart form data.
3. A text follow-up with **Use latest image** enabled edits the latest **successful generated** image from that chat.
4. An explicit new upload **replaces** that image reference, even if Use latest image is enabled. Only one reference image is sent.
5. Earlier successful text instructions are sent as bounded context. The original instruction, recent whole turns and the current request are prioritized. Failed turns are excluded. Very old follow-ups can be omitted in long chats; full history remains saved locally.
6. Unchecking Use latest image stops automatic image reuse, but **does not clear text history**. Select **New chat** for an independent generation.
7. An image with no text gets the visible default prompt: “Create a new variation of this image, preserving its main subject and style.” It is not interpreted as an image-analysis question.

Assistant captions such as “Here's your image” are generated by the application, not returned text from the model. Each request produces one PNG image. GPT-image-2 sizes: 1024×1024, 1536×1024, 1024×1536, 1536×1536 and 3840×2160. MAI's options differ as described below. Larger sizes and higher quality may increase latency and cost.

### Choose a model

Use the **Model** selector in the composer to choose **GPT-image-2**, **MAI-Image-2.5-Flash (Preview)** or **FLUX.2-pro**. Selection is per draft, not a global server switch: parallel chats can use different models. New chats default to GPT-image-2. When reopening a saved conversation, its last saved model is selected when known; subsequent polls never overwrite an existing draft choice.

All three models support text-to-image generation, reference-image editing and this app's image-only default variation prompt. A follow-up can switch models and reuse the previous model's generated PNG. The explicit attachment still takes precedence over automatic latest-image reuse. Provider calls are stateless; successful prior instructions are included as context. This app accepts **one reference image per turn**, even when a provider supports multiple references.

| Option | GPT-image-2 | MAI-Image-2.5-Flash |
| --- | --- | --- |
| Model status | Independent configuration readiness | Independent configuration readiness; preview service |
| Generation sizes offered | Square, portrait, landscape and 4K presets | 1024×1024, 768×1024, 1024×768, 768×1365, 1365×768 |
| Edit output size | Selected preset | Model determined (`auto` internally); no dimensions sent |
| Quality | low / medium / high | Model default; no quality parameter sent |
| Endpoint family | Azure OpenAI Images API | MAI Images API |

**FLUX.2-pro:** generation and editing both offer 1024×1024, 1536×1024, 1024×1536, 1536×1536 and 1920×1920. These conservative presets stay below its documented 4 MP output cap; they are not an exhaustive list of supported dimensions. Quality is model-default (the quality selector is disabled). Unlike MAI, FLUX edits accept a selected size; unlike GPT, the 3840×2160 preset is not offered. Switching models normalizes incompatible options before submission.

MAI generation requires **each edge at least 768 pixels and total pixels at most 1,048,576**. GPT quality/4K settings are not sent to MAI. The UI resets incompatible settings when changing models or entering/leaving MAI edit mode; disabled controls explain model-default behavior. The server independently validates these options before calling the provider. There is **no fallback to another model** on missing configuration, errors, or throttling.

Saved turns and administrator telemetry include the **model ID and deployment name actually selected**. Existing records without model provenance stay labelled **Legacy / unknown model**; the app does not guess their historical deployment. An attempt throttled before parsing its form may likewise have unknown model metadata. Generation/edit counters and the shared five-attempts-per-minute limiter cover all three models.

Generation has a default five-minute timeout. **Different chats can generate images concurrently.** Only one generation/operation can run within a given chat at a time; concurrent submissions to that same chat receive HTTP 409 rather than racing or silently queuing. The five-request-per-minute application limit still applies **across all chats**, and Foundry quotas can be stricter. Parallel generation can use quota and incur charges faster.

### Work in parallel

1. Submit a prompt in one chat, then select **New chat** or open another conversation in History while it generates.
2. Each chat keeps its own draft, attachment, quality, size and reference-image choice **in this browser tab's memory**. Switching chats does not discard drafts. Reloading or closing the page loses unsent drafts/attachments; saved conversations remain on the server.
3. The sidebar shows each running chat's progress and background completion/failure. A result only updates its own conversation; it cannot replace the active chat or another draft. The active generating chat's composer is disabled, but other chats remain usable.
4. Busy chats cannot be deleted until their operation finishes. Other chats can still be created or deleted.
5. Saved status is checked automatically using GET-only polling roughly every three seconds (after the previous check completes). Reopening/reloading recovers running turns, including work started in another tab. Transient read failures are retried without resubmitting generation.

If the browser disconnects, the backend continues and saves the result. Check the saved chat **before retrying**. An interrupted POST response may be uncertain: the app keeps that chat's draft and offers **Check saved result** and **Result reviewed** rather than attributing another tab's result to it. Reviewing an uncertain result only unlocks the draft; it does not send a request. Other chats remain available during recovery. Billable POSTs are deliberately not automatically retried.

## Foundry API configuration

### GPT-image-2 (existing settings unchanged)

The default follows the documented deployment-scoped Images API:

```text
POST {Endpoint}/openai/deployments/{Deployment}/images/generations?api-version=2025-04-01-preview
POST {Endpoint}/openai/deployments/{Deployment}/images/edits?api-version=2025-04-01-preview
```

If your deployment's **View code** sample instead uses the OpenAI v1 API, set **both** `Foundry:ApiStyle` to `v1` and `Foundry:ApiVersion` to `preview`. This selects `/openai/v1/images/generations` or `/openai/v1/images/edits` and passes the deployment name in `model`. API versions and support can differ between deployments; use the values supported by yours.

Reference: [Microsoft Foundry image generation and editing documentation](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/dall-e).

### MAI-Image-2.5-Flash (separate server-side configuration)

Keep your current `Foundry` settings for GPT. **Merge** the `Foundry.Mai` section from [appsettings.Models.example.json](appsettings.Models.example.json) into your ignored local settings; do not replace the whole file or remove your GPT credentials. The example is documentation only and is not automatically loaded.

Set `Foundry:Mai:Endpoint` to the MAI resource root, normally `https://YOUR-RESOURCE.services.ai.azure.com/`, `Foundry:Mai:Deployment` to your exact deployed name, and supply its key locally or choose `DefaultAzureCredential`. Even if both deployments share a resource, configure them explicitly; the app does not send the GPT key to a guessed MAI host or auto-derive resource URLs.

MAI requests use:

```text
POST {Foundry:Mai:Endpoint}/mai/v1/images/generations
POST {Foundry:Mai:Endpoint}/mai/v1/images/edits
```

Generation sends JSON containing `model`, `prompt`, `width`, `height`. Editing sends multipart `model`, `prompt`, `image`. No OpenAI API-version query, `quality`, `size`, `n`, or `output_format` is sent to MAI. The response is a base64 PNG, as with GPT. MAI-Image-2.5-Flash is currently documented as **preview**, without a production SLA; deployment-specific access and quotas apply.

Reference: [MAI image models in Microsoft Foundry](https://learn.microsoft.com/azure/foundry/foundry-models/how-to/use-foundry-models-mai-image).

### FLUX.2-pro (independent BFL provider configuration)

Merge the **`Foundry.Flux`** section from [appsettings.Models.example.json](appsettings.Models.example.json) into your ignored local settings. Leave existing GPT and MAI settings intact. Configure its resource-root `Endpoint`, actual `Deployment` name, and key or `DefaultAzureCredential`; restart the server after editing settings. Default deployment is `FLUX.2-pro` and default API version is `preview`. The selector submits the fixed application ID `flux.2-pro`, not a URL or arbitrary deployment name.

The Foundry FLUX.2-pro provider uses a different API and key header:

```text
POST {Foundry:Flux:Endpoint}/providers/blackforestlabs/v1/flux-2-pro?api-version=preview
Authorization: Bearer <resource API key or Microsoft Entra token>
```

Use the resource root from your Foundry deployment's **View code** sample, e.g. `https://YOUR-RESOURCE.api.cognitive.microsoft.com/`; do not enter the full operation URL. The model path `flux-2-pro` is fixed; the JSON `model` value uses the configured deployment name. GPT/MAI credentials are never inherited or sent to a guessed FLUX host.

Both text generation and editing send JSON with `model`, `prompt`, `width`, `height`, `output_format: "png"`, and `num_images: 1`. Editing adds the single reference as raw-base64 `input_image` (not multipart, a URL, or a data-URL). GPT/MAI `quality`, `size`, `n` and `response_format` parameters are not sent, and provider content safety defaults are unchanged.

The app consumes the synchronous Azure response shown in Microsoft's sample: **`data[0].b64_json`** containing PNG bytes. Direct BFL async `polling_url` / `result.sample` responses or URL-only responses are **not** followed, and credentials are never forwarded to returned URLs. Those responses produce a safe error rather than a speculative download or provider fallback. Microsoft's guide allows URL or base64 responses, so confirm your deployed endpoint returns the supported base64 contract; no live invocation is performed by setup/tests.

References: [FLUX in Microsoft Foundry](https://learn.microsoft.com/azure/foundry/foundry-models/how-to/use-foundry-models-flux), [Microsoft's Azure FLUX provider sample](https://github.com/microsoft-foundry/foundry-samples/blob/main/samples/python/black-forest-labs/flux/README.md). FLUX.2-pro is integrated through the BFL provider API; the FLUX.1 OpenAI-compatible API examples are not used for it.

All settings can be overridden by environment variables (double underscore replaces colon). Environment variables take precedence over the local file; command-line configuration comes last.

| Environment variable | Default | Meaning |
| --- | --- | --- |
| `Foundry__Endpoint` | Empty | HTTPS resource root |
| `Foundry__Deployment` | `gpt-image-2` | Deployed model's deployment name |
| `Foundry__AuthMode` | `ApiKey` | `ApiKey` or `DefaultAzureCredential` |
| `Foundry__ApiKey` | Empty | Server-only resource key |
| `Foundry__ApiStyle` | `deployments` | `deployments` or `v1` |
| `Foundry__ApiVersion` | `2025-04-01-preview` | Use `preview` for v1 if your deployment supports it |
| `Foundry__TokenScope` | `https://cognitiveservices.azure.com/.default` | Entra token audience; configurable for your endpoint |
| `Foundry__TimeoutSeconds` | `300` | Clamped to 30–600 seconds |
| `Foundry__Mai__Endpoint` | Empty | MAI HTTPS resource root, configured independently |
| `Foundry__Mai__Deployment` | `mai-image-2.5-flash` | MAI deployment name, not necessarily the model catalog name |
| `Foundry__Mai__AuthMode` | `ApiKey` | `ApiKey` or `DefaultAzureCredential` |
| `Foundry__Mai__ApiKey` | Empty | MAI server-only resource key |
| `Foundry__Mai__TokenScope` | `https://cognitiveservices.azure.com/.default` | MAI Entra token audience |
| `Foundry__Mai__TimeoutSeconds` | `300` | MAI timeout, clamped to 30–600 seconds |
| `Foundry__Flux__Endpoint` | Empty | Independent FLUX HTTPS resource root |
| `Foundry__Flux__Deployment` | `FLUX.2-pro` | FLUX deployment name |
| `Foundry__Flux__ApiVersion` | `preview` | Provider API version |
| `Foundry__Flux__AuthMode` | `ApiKey` | `ApiKey` (Bearer header) or `DefaultAzureCredential` |
| `Foundry__Flux__ApiKey` | Empty | FLUX server-only resource key |
| `Foundry__Flux__TokenScope` | `https://cognitiveservices.azure.com/.default` | FLUX Entra token audience |
| `Foundry__Flux__TimeoutSeconds` | `300` | FLUX timeout, clamped to 30–600 seconds |
| `Storage__Directory` | `data` | Relative to content root, or an absolute mounted data directory |
| `Security__RequireHttps` | `false` | Use `true` for cloud/public hosting: reject HTTP and always mark cookies Secure |
| `ReverseProxy__Enabled` | `false` | Process forwarded IP/protocol only from explicit trusted ingress addresses |
| `ReverseProxy__KnownProxies__0` | Not set | First trusted proxy IP (additional entries use `__1`, etc.) |
| `ReverseProxy__KnownNetworks__0` | Not set | First trusted ingress CIDR; never use a trust-all network |
| `URLS` | `http://localhost:5080` | Listening address; container sets port 8080 |
| `AllowedHosts` | localhost and loopback IPs | Semicolon-separated allowed hostnames; set your cloud hostname explicitly |

### Keyless authentication

Set `Foundry:AuthMode` to `DefaultAzureCredential`. A pre-existing supported local developer login can be used, or a managed identity when hosted in Azure. This repo does not sign you in or change Azure account settings. Assign the identity the **Cognitive Services OpenAI User** role on the model resource. A basic local Docker container does not automatically inherit the host's developer login; API-key auth is the simplest local-container option.

For cloud hosting, prefer managed identity and an appropriate token scope for the API endpoint, or inject a Key Vault-backed secret through the host platform. No key is ever returned from the status endpoint or sent to the browser.

## Persistence and privacy

Chat text, image bytes, ownership and telemetry live in `data/imagechat.db` (plus SQLite WAL/SHM files while running). Accounts are in `data/users.json`; cookie-protection keys are in `data/keys/`. Data survives server/browser restarts. Browser localStorage holds only the selected chat ID; authentication uses a separate HttpOnly cookie, not JavaScript-accessible credentials. Images are served only by authorized database IDs, never user-provided filesystem paths.

- Inputs are limited to one PNG/JPEG, maximum 20 MB, and 8,000 prompt characters. The server validates file signatures; it does not fully decode/re-encode uploads. The browser also checks that an image can decode. Foundry performs its own validation and moderation.
- **Prompts and referenced images leave your machine and are sent to your Foundry deployment.** Local hosting does not mean local inference. Send only content you are authorized to process.
- Deleting a chat deletes its database rows and image references. This is logical deletion, not a guarantee of forensic erasure from SQLite pages, WAL, backups or storage snapshots.
- To back up or move the app's history, stop the server and copy the **entire data directory**. Do not copy only the main database while the server is running. For live backups, use SQLite's backup API instead.
- Configuration and data directories are not encrypted by the application. Passwords are hashed, but usernames, IP telemetry and cookie-protection keys still require OS permissions and encrypted storage. Protect keys with platform-managed encryption/Key Vault or an appropriate certificate integration for production; the default persistent key files may produce an expected ASP.NET warning that no XML encryptor is configured. Never expose them or share the storage with untrusted users.
- Failed requests are saved as failed turns. On restart, previously pending turns are marked interrupted so they do not remain pending forever.

## Docker / portable cloud packaging

Docker is optional; **no container or cloud deployment is created automatically**.

Create a git-ignored `.env` file locally for Compose:

```dotenv
FOUNDRY_ENDPOINT=https://YOUR-RESOURCE.openai.azure.com/
FOUNDRY_DEPLOYMENT=YOUR-DEPLOYMENT-NAME
FOUNDRY_API_KEY=YOUR-KEY
MAI_ENDPOINT=https://YOUR-MAI-RESOURCE.services.ai.azure.com/
MAI_DEPLOYMENT=YOUR-MAI-DEPLOYMENT-NAME
MAI_API_KEY=YOUR-MAI-KEY
FLUX_ENDPOINT=https://YOUR-FLUX-RESOURCE.api.cognitive.microsoft.com/
FLUX_DEPLOYMENT=FLUX.2-pro
FLUX_API_KEY=YOUR-FLUX-KEY
```

Then:

```sh
docker compose up --build
```

On the first container start, bootstrap using your own interactive terminal:

```sh
docker compose exec -it imagechat dotnet ImageChat.dll --create-admin
docker compose restart imagechat
```

Open http://localhost:5080/login. Compose binds only to loopback and uses a named volume for persistence. The image runs as non-root and does not contain local settings, accounts, key files, uploads, test artifacts or development caches. `docker compose down` retains data; **adding `--volumes` deletes the named data volume**, including accounts and history. Docker maintains its own external image/volume storage when you choose this option.

For a future cloud deployment:

1. Build/push the Docker image using your chosen platform; listen on port 8080.
2. Configure Foundry settings as environment variables/managed identity. Set `AllowedHosts` to the public hostname.
3. Mount a writable persistent volume at `/app/data`, accessible only to the container's non-root user and trusted operators. Persist the entire directory, including users and cookie-protection keys. Use storage with SQLite-compatible locking and atomic file rename; avoid multi-writer network filesystems. Bootstrap the administrator via a protected interactive operator session, not an internet-accessible setup URL.
4. Run **one application instance** with this SQLite implementation. Parallel chats are supported within that instance; startup recovery and the per-chat operation leases assume one process. Network/model work runs concurrently; SQLite writes remain short serialized transactions. For horizontal scaling, replace `ChatStore` with a managed database/object store, use distributed job coordination and persist idempotency keys.
5. Enable TLS, `Security__RequireHttps=true`, the actual public `AllowedHosts` and the narrowly trusted ingress settings described above **before exposing it publicly**. File-based login includes roles and user isolation, but not MFA, email verification, self-registration or federated recovery. Prefer Microsoft Entra/platform identity for enterprise/public deployments; add perimeter abuse controls and operational security monitoring.
6. Configure proxy timeouts to allow the generation timeout plus overhead, or move generation into a durable background queue for hosting platforms with short request limits. Keep uploaded/image response size limits aligned.
7. Use `/healthz` for process health. It does not test Foundry availability or credentials. Set backups, storage quotas, retention, monitoring and budget alerts.

The app is portable to a **single-instance** cloud container with durable storage and explicit network/security configuration. Built-in login is not a substitute for a production threat assessment, perimeter protection and account-recovery/retention policies. No Azure resources, role assignments or deployments are modified by this project.

## Test and publish

```powershell
powershell -NoProfile -File scripts/dev.ps1 test
powershell -NoProfile -File scripts/dev.ps1 build
powershell -NoProfile -File scripts/dev.ps1 publish
```

Or `sh scripts/dev.sh test|build|publish` on Linux/macOS. Publishing writes to `.cache/publish/`; inject configuration separately. The committed package lock files pin restored dependencies. Tests run in Release configuration so they can run alongside the Debug development server on Windows without overwriting its locked executable. Tests use fake HTTP/model responses and isolated repository-local test databases; they do not call Foundry.

## Troubleshooting

- **Setup needed**: fill in the local settings and restart. Ensure your deployment name matches exactly.
- **401/403**: verify the key or Entra role/token audience. A private endpoint also needs suitable network access.
- **404**: verify resource root, deployment name, API style and version against Foundry's View code sample.
- **400/422**: check image contents, supported size/quality, prompt restrictions and service content filtering. No filters are bypassed.
- **429**: wait before trying again or review deployment quota. The app does not retry automatically.
- **Timeout or lost response**: check history before resubmitting. The model request might still be billed even if no image was saved.
- **Port already in use**: stop the existing process or set `URLS` to another localhost port.
- **NuGet restore fails**: use an accessible approved NuGet feed. Do not disable TLS verification; package feeds are environment configuration, not a hard-coded dependency of this repo.

## Layout

```text
Program.cs               HTTP API, middleware and dependency injection
FoundryClient.cs         Foundry image generation/editing and authentication
ChatStore.cs             SQLite persistence
ChatOperations.cs        Per-chat generation/deletion leases
UserStore.cs             Atomic account file and salted password hashing
AccountSecurity.cs       Cookies, session validation and account endpoints
BootstrapAdmin.cs        Offline interactive administrator setup/password reset
Telemetry.cs             Request records, legacy migration and filtered totals
ProxySecurity.cs         Explicit trusted forwarded-IP/protocol configuration
Models.cs                Contracts, validation and conversation context
wwwroot/                 Dependency-free HTML/CSS/JavaScript website
tests/                   Unit tests and in-process API integration tests
scripts/                 Repo-contained run/test/build/publish scripts
Dockerfile, compose.yaml Optional container packaging
```