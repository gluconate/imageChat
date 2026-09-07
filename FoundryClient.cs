using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace ImageChat;

public sealed class FoundryOptions
{
    // Existing root settings remain the GPT deployment; MAI is configured independently.
    public MaiOptions Mai { get; set; } = new();
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "gpt-image-2";
    public string ApiVersion { get; set; } = "2025-04-01-preview";
    public string ApiStyle { get; set; } = "deployments";
    public string AuthMode { get; set; } = "ApiKey";
    public string ApiKey { get; set; } = "";
    public string TokenScope { get; set; } = "https://cognitiveservices.azure.com/.default";
    public int TimeoutSeconds { get; set; } = 300;

    public string? ConfigurationError(bool allowMai = false)
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath.Trim('/') != "" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return "Set Foundry:Endpoint to the HTTPS resource root (not a project URL or complete API URL) in appsettings.Local.json, then restart.";
        if (string.IsNullOrWhiteSpace(Deployment)) return "Set Foundry:Deployment to your deployed model's deployment name.";
        if (ApiStyle is not ("deployments" or "v1") && !(allowMai && ApiStyle == "mai")) return "Foundry:ApiStyle must be deployments or v1.";
        if (string.IsNullOrWhiteSpace(ApiVersion)) return "Set Foundry:ApiVersion to the version from your Foundry sample.";
        if (AuthMode is not ("ApiKey" or "DefaultAzureCredential")) return "Foundry:AuthMode must be ApiKey or DefaultAzureCredential.";
        if (AuthMode == "ApiKey" && string.IsNullOrWhiteSpace(ApiKey)) return "Set Foundry:ApiKey locally, or use DefaultAzureCredential, then restart.";
        return null;
    }

    public Uri RequestUri(bool editing)
    {
        if (ApiStyle == "mai") return new Uri($"{Endpoint.TrimEnd('/')}/mai/v1/images/{(editing ? "edits" : "generations")}");
        var path = ApiStyle == "v1" ? "openai/v1/images" : $"openai/deployments/{Uri.EscapeDataString(Deployment)}/images";
        return new Uri($"{Endpoint.TrimEnd('/')}/{path}/{(editing ? "edits" : "generations")}?api-version={Uri.EscapeDataString(ApiVersion)}");
    }
}

public interface IImageGenerator
{
    Task<ImageData> GenerateAsync(GenerationInput input, CancellationToken cancellationToken);
}

public sealed class FoundryClient(HttpClient client, FoundryOptions options, TokenCredential credential) : IImageGenerator
{
    public async Task<ImageData> GenerateAsync(GenerationInput input, CancellationToken cancellationToken)
    {
        // Resolve per call; never mutate shared options/headers across parallel models.
        var selected = new ModelCatalog(options).Resolve(input.Model);
        var connection = selected.Connection;
        selected.Validate(input.Size, input.Quality, input.Image != null);
        if (selected.ConfigurationError is { } error) throw new AppException(503, error);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(connection.TimeoutSeconds, 30, 600)));
        using var request = new HttpRequestMessage(HttpMethod.Post, connection.RequestUri(input.Image is not null));
        if (connection.AuthMode == "ApiKey") request.Headers.Add("api-key", connection.ApiKey);
        else
        {
            try
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext([connection.TokenScope]), timeout.Token);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            }
            catch (AuthenticationFailedException)
            {
                throw new AppException(502, "Azure authentication failed. Check the configured identity and its Cognitive Services OpenAI User role.");
            }
        }
        if (input.Image is { } image)
        {
            var form = new MultipartFormDataContent();
            request.Content = form;
            form.Add(new StringContent(connection.Deployment), "model");
            form.Add(new StringContent(input.Prompt), "prompt");
            if (selected.Id == ModelCatalog.Gpt)
            {
                form.Add(new StringContent(input.Size), "size");
                form.Add(new StringContent(input.Quality), "quality");
                form.Add(new StringContent("1"), "n");
                form.Add(new StringContent("png"), "output_format");
            }
            var file = new ByteArrayContent(image.Bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
            form.Add(file, "image", image.ContentType == "image/png" ? "reference.png" : "reference.jpg");
        }
        else if (selected.Id == ModelCatalog.Mai)
        {
            var dimensions = input.Size.Split('x');
            request.Content = JsonContent.Create(new { model = connection.Deployment, prompt = input.Prompt,
                width = int.Parse(dimensions[0]), height = int.Parse(dimensions[1]) });
        }
        else
            request.Content = JsonContent.Create(new { model = connection.Deployment, prompt = input.Prompt,
                size = input.Size, quality = input.Quality, n = 1, output_format = "png" });

        // Do not retry billable POSTs automatically: a lost response may still have produced an image.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            var message = code switch
            {
                401 or 403 => "Foundry rejected authentication. Check your API key or identity role assignments.",
                404 => "Foundry endpoint or deployment was not found. Check the resource root, deployment name, API style and version.",
                429 => "Foundry is rate limiting requests. Wait a minute before trying again; check your deployment quota.",
                400 or 422 => "Foundry rejected the prompt, image or options. Content filtering may apply; check the deployment's supported API version and settings.",
                _ => "Foundry could not complete the request. Try again later."
            };
            throw new AppException(code == 429 ? 429 : 502, message);
        }

        // Bound response memory even if the upstream sends a malformed/unexpected payload.
        const int maximumResponseBytes = 48 * 1024 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
        {
            if (buffer.Length + read > maximumResponseBytes) throw new AppException(502, "Foundry returned an image response larger than the app's limit.");
            buffer.Write(chunk, 0, read);
        }
        try
        {
            using var json = JsonDocument.Parse(buffer.ToArray());
            if (!json.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() != 1 ||
                !data[0].TryGetProperty("b64_json", out var encoded) || encoded.ValueKind != JsonValueKind.String)
                throw new AppException(502, "Foundry did not return a generated image. The output may have been filtered.");
            var bytes = Convert.FromBase64String(encoded.GetString()!);
            if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new AppException(502, "Foundry returned an unexpected image format; PNG was requested.");
            return new ImageData(bytes, "image/png");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw new AppException(502, "Foundry returned an invalid image response.");
        }
    }
}