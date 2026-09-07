namespace ImageChat;

public sealed class MaiOptions
{
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "mai-image-2.5-flash";
    public string AuthMode { get; set; } = "ApiKey";
    public string ApiKey { get; set; } = "";
    public string TokenScope { get; set; } = "https://cognitiveservices.azure.com/.default";
    public int TimeoutSeconds { get; set; } = 300;
}

public sealed class FluxOptions
{
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "FLUX.2-pro";
    public string ApiVersion { get; set; } = "preview";
    public string AuthMode { get; set; } = "ApiKey";
    public string ApiKey { get; set; } = "";
    public string TokenScope { get; set; } = "https://cognitiveservices.azure.com/.default";
    public int TimeoutSeconds { get; set; } = 300;
}

public sealed record ModelStatus(string Id, string Label, bool Configured, string Deployment, string AuthMode,
    string Message, bool SupportsEditing, IReadOnlyList<string> Sizes, IReadOnlyList<string> EditSizes, IReadOnlyList<string> Qualities);

public sealed record SelectedModel(string Id, string Label, FoundryOptions Connection, IReadOnlyList<string> Sizes,
    IReadOnlyList<string> EditSizes, IReadOnlyList<string> Qualities)
{
    public string? ConfigurationError => Connection.ConfigurationError(allowMai: Id == ModelCatalog.Mai, allowFlux: Id == ModelCatalog.Flux) is { } error
        ? error.Replace("Foundry:", Id switch
        {
            ModelCatalog.Mai => "Foundry:Mai:",
            ModelCatalog.Flux => "Foundry:Flux:",
            _ => "Foundry:"
        }) : null;

    public ModelStatus Status() => new(Id, Label, ConfigurationError == null, Connection.Deployment, Connection.AuthMode,
        ConfigurationError ?? "Configuration ready. Connectivity is checked when you generate an image.", true, Sizes, EditSizes, Qualities);

    public void Validate(string size, string quality, bool editing)
    {
        if (!(editing ? EditSizes : Sizes).Contains(size) || !Qualities.Contains(quality))
            throw new AppException(400, Id switch
            {
                ModelCatalog.Mai => "MAI-Image-2.5-Flash uses model-default quality, up-to-1MP generation sizes, and auto size for edits.",
                ModelCatalog.Flux => "FLUX.2-pro uses model-default quality and the offered size presets for generation and editing.",
                _ => "Choose one of the supported GPT-image-2 sizes and quality settings."
            });
    }
}

public sealed class ModelCatalog(FoundryOptions options)
{
    public const string Gpt = "gpt-image-2";
    public const string Mai = "mai-image-2.5-flash";
    public const string Flux = "flux.2-pro";
    private static readonly string[] GptSizes = ["1024x1024", "1536x1024", "1024x1536", "1536x1536", "3840x2160"];
    private static readonly string[] MaiSizes = ["1024x1024", "768x1024", "1024x768", "768x1365", "1365x768"];
    // Conservative presets below the documented 4 MP cap (not a claim of every supported size).
    private static readonly string[] FluxSizes = ["1024x1024", "1536x1024", "1024x1536", "1536x1536", "1920x1920"];

    // Only stable allowlisted IDs are accepted, never user-supplied endpoints/deployments.
    public SelectedModel Resolve(string? id) => id switch
    {
        null or Gpt => new(Gpt, "GPT-image-2", options, GptSizes, GptSizes, new[] { "low", "medium", "high" }),
        Mai => new(Mai, "MAI-Image-2.5-Flash (Preview)", new FoundryOptions
        {
            Endpoint = options.Mai.Endpoint, Deployment = options.Mai.Deployment, ApiStyle = "mai",
            AuthMode = options.Mai.AuthMode, ApiKey = options.Mai.ApiKey,
            TokenScope = options.Mai.TokenScope, TimeoutSeconds = options.Mai.TimeoutSeconds
        }, MaiSizes, new[] { "auto" }, new[] { "default" }),
        Flux => new(Flux, "FLUX.2-pro", new FoundryOptions
        {
            Endpoint = options.Flux.Endpoint, Deployment = options.Flux.Deployment, ApiStyle = "bfl",
            ApiVersion = options.Flux.ApiVersion, AuthMode = options.Flux.AuthMode, ApiKey = options.Flux.ApiKey,
            TokenScope = options.Flux.TokenScope, TimeoutSeconds = options.Flux.TimeoutSeconds
        }, FluxSizes, FluxSizes, new[] { "default" }),
        _ => throw new AppException(400, "Choose a supported image model.")
    };

    public ModelStatus[] Statuses() => [Resolve(Gpt).Status(), Resolve(Mai).Status(), Resolve(Flux).Status()];
}