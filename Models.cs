namespace ImageChat;

public sealed record ChatSummary(string Id, string Title, string CreatedAt, string UpdatedAt, int MessageCount, string? ThumbnailUrl, bool IsGenerating = false);
public sealed record Chat(string Id, string Title, string CreatedAt, string UpdatedAt, List<ChatMessage> Messages);
public sealed record ChatMessage(string Id, string Role, string Text, string? ImageUrl, string CreatedAt,
    string Size, string Quality, string Status, string? Error, string? Model = null, string? Deployment = null);
public sealed record ImageData(byte[] Bytes, string ContentType);
public sealed record GenerationInput(string Prompt, string Size, string Quality, ImageData? Image, string Model = ModelCatalog.Gpt);
public sealed class AppException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class InputRules
{
    public const int MaxImageBytes = 20 * 1024 * 1024;
    public const int MaxPromptCharacters = 8000;
    public const string ImageOnlyPrompt = "Create a new variation of this image, preserving its main subject and style.";

    public static string ValidatePrompt(string prompt, bool hasImage)
    {
        prompt = prompt.Trim();
        if (prompt.Length > MaxPromptCharacters)
            throw new AppException(400, "Keep the prompt under 8,000 characters.");
        if (prompt.Length == 0 && !hasImage)
            throw new AppException(400, "Add a prompt or attach an image.");
        return prompt.Length == 0 ? ImageOnlyPrompt : prompt;
    }

    public static void ValidateOptions(string size, string quality)
    {
        string[] sizes = ["1024x1024", "1536x1024", "1024x1536", "1536x1536", "3840x2160"];
        if (!sizes.Contains(size) || !new[] { "low", "medium", "high" }.Contains(quality))
            throw new AppException(400, "Choose one of the supported sizes and quality settings.");
    }

    public static ImageData ValidateImage(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
            throw new AppException(400, "Images must be between 1 byte and 20 MB.");
        if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return new(bytes, "image/png");
        if (bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255)
            return new(bytes, "image/jpeg");
        throw new AppException(400, "Only PNG and JPEG images are supported. The file contents must match the format.");
    }

    // The Images API is stateless. Carry forward prior successful instructions and the latest image.
    // Keep whole recent turns within a bounded prompt, prioritizing the first instruction and current request.
    public static string BuildPrompt(Chat chat, string prompt)
    {
        var successful = new List<string>();
        for (var i = 0; i + 1 < chat.Messages.Count; i++)
            if (chat.Messages[i].Role == "user" && chat.Messages[i + 1] is { Role: "assistant", Status: "completed" })
                successful.Add(chat.Messages[i].Text);
        if (successful.Count == 0) return prompt;
        var first = successful[0];
        var budget = 28000 - prompt.Length - first.Length;
        var recent = new List<string>();
        foreach (var text in successful.Skip(1).Reverse())
        {
            if (text.Length + 32 > budget) break;
            recent.Insert(0, text);
            budget -= text.Length + 32;
        }
        return "Continue this image-creation session. Earlier instructions are context; the current request takes precedence. " +
            "If an image is supplied, use it as the visual reference.\n\nOriginal request:\n" + first +
            string.Concat(recent.Select(text => "\n\nEarlier follow-up:\n" + text)) + "\n\nCurrent request:\n" + prompt;
    }
}