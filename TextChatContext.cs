namespace ImageChat;

public sealed record ChatContextResult(TextChatInput Input, bool Truncated);

/// <summary>
/// Builds context from one already-authorized chat snapshot (obtained with the caller's
/// owner ID). No cross-chat search, external URLs, provider calls, or shared history.
/// Character/message/image caps are application budgets, not model token limits.
/// </summary>
public static class TextChatContext
{
    public const string ImageOnlyPrompt = "Describe this image and help me understand it.";
    public const string GeneratedImageReference = "[Generated image reference from an earlier turn]";

    public static string ValidatePrompt(string prompt, bool hasImage, ChatModelOptions options)
    {
        if (options.ContextConfigurationError() is { } error) throw new AppException(503, error);
        if (hasImage && !options.SupportsImages)
            throw new AppException(400, "Images are disabled for this chat model.");
        prompt = (prompt ?? "").Trim();
        if (prompt.Length == 0)
        {
            if (!hasImage) throw new AppException(400, "Add a prompt or attach an image.");
            prompt = ImageOnlyPrompt;
        }
        if (prompt.Length > options.MaxPromptCharacters)
            throw new AppException(400, $"Keep the chat prompt within {options.MaxPromptCharacters:N0} characters.");
        return prompt;
    }

    public static ChatContextResult Build(Chat chat, string prompt, ImageData? attachment,
        IChatStore store, string ownerId, ChatModelOptions options)
    {
        prompt = ValidatePrompt(prompt, attachment is not null, options);
        // Never allow the store's optional/unscoped owner overload to be used accidentally.
        if (string.IsNullOrWhiteSpace(ownerId)) throw new AppException(404, "Chat not found.");

        ImageData? currentImage = null;
        if (attachment is not null)
        {
            if (attachment.Bytes is null) throw new AppException(400, "Supply a valid PNG or JPEG image.");
            currentImage = InputRules.ValidateImage(attachment.Bytes);
            if (currentImage.Bytes.Length > options.MaxContextImageBytes)
                throw new AppException(400, "The attached image exceeds the chat image byte budget.");
        }
        var current = new ChatInputMessage("user", prompt, currentImage is null ? null : [currentImage]);
        var characters = prompt.Length;
        var messageCount = 1;
        var truncated = false;
        var turns = new List<List<ContextMessage>>(); // Newest first; no image bytes loaded yet.

        for (var index = chat.Messages.Count - 1; index > 0; index--)
        {
            var assistant = chat.Messages[index];
            var user = chat.Messages[index - 1];
            if (user is not { Role: "user", Status: "completed", Error: null, Text: not null } ||
                assistant is not { Role: "assistant", Status: "completed", Error: null, Text: not null })
                continue;
            index--; // Only adjacent, fully successful pairs constitute historical turns.

            var turn = new List<ContextMessage> { new(user, "user", user.Text, user.ImageUrl is not null) };
            if (assistant.ImageUrl is not null)
            {
                // Image-generation boilerplate is never an assistant text answer.
                if (options.SupportsImages)
                    turn.Add(new(assistant, "user", GeneratedImageReference, true));
                else truncated = true;
            }
            else if (assistant.Model == ChatModelOptions.ModelId)
                turn.Add(new(assistant, "assistant", assistant.Text, false));
            else if (user.ImageUrl is null)
            {
                // Do not pass unrecognized/legacy non-text answers as chat assistant output.
                truncated = true;
                continue;
            }

            // Reserve the actual candidate messages, including generated-image references.
            // Stop at the first oversized whole turn; never skip it to backfill older turns.
            var turnCharacters = turn.Sum(message => (long)message.Text.Length);
            if (turn.Count > options.MaxContextMessages - messageCount ||
                turnCharacters > options.MaxContextCharacters - characters)
            {
                truncated = true;
                break;
            }
            characters += (int)turnCharacters;
            messageCount += turn.Count;
            turns.Add(turn);
        }

        var imageCount = currentImage is null ? 0 : 1;
        var imageBytes = currentImage?.Bytes.Length ?? 0;
        var stopImages = !options.SupportsImages;
        foreach (var turn in turns)
        {
            // Prioritize newest generated references over the same turn's input attachment.
            for (var index = turn.Count - 1; index >= 0; index--)
            {
                var message = turn[index];
                if (!message.HasImage) continue;
                if (stopImages || imageCount >= options.MaxContextImages || imageBytes >= options.MaxContextImageBytes)
                {
                    stopImages = true;
                    truncated = true;
                    continue;
                }

                // Only selected turns are read, always by message ID plus owner ID. ImageUrl
                // is a presence flag, never a URL to fetch. Even persisted/generated images
                // must pass the same PNG/JPEG signature and 20 MB per-image input limit.
                ImageData image;
                try
                {
                    var stored = store.GetImage(message.Source.Id, ownerId);
                    if (stored?.Bytes is null) throw UnavailableImage();
                    image = InputRules.ValidateImage(stored.Bytes);
                }
                catch (AppException)
                {
                    // Never silently claim a missing, unauthorized, or invalid image was sent.
                    throw UnavailableImage();
                }
                catch (Azure.RequestFailedException)
                {
                    // Cloud metadata can outlive an unavailable blob. Do not leak storage
                    // diagnostics or fall back to text as if that image had been included.
                    throw UnavailableImage();
                }
                if (image.Bytes.Length > options.MaxContextImageBytes - imageBytes)
                {
                    // One bounded lookahead, then stop: never load all older images looking
                    // for one that fits. Retained bytes/count stay within the configured caps.
                    stopImages = true;
                    truncated = true;
                    continue;
                }
                message.Image = image;
                imageCount++;
                imageBytes += image.Bytes.Length;
            }
        }

        var messages = new List<ChatInputMessage>(messageCount);
        for (var index = turns.Count - 1; index >= 0; index--)
            foreach (var message in turns[index])
            {
                // An omitted generated image has no standalone placeholder or boilerplate.
                // Original requests/text answers survive optional image omission; the caller
                // must surface Truncated so omission is not presented as full visual context.
                if (message.Source.Role == "assistant" && message.HasImage && message.Image is null) continue;
                messages.Add(new(message.Role, message.Text, message.Image is null ? null : [message.Image]));
            }
        messages.Add(current);
        return new(new(messages.ToArray()), truncated);
    }

    private static AppException UnavailableImage() => new(409,
        "An earlier image is unavailable or cannot be used for chat. Attach a supported PNG or JPEG image (up to 20 MB) in a new chat.");

    private sealed class ContextMessage(ChatMessage source, string role, string text, bool hasImage)
    {
        public ChatMessage Source { get; } = source;
        public string Role { get; } = role;
        public string Text { get; } = text;
        public bool HasImage { get; } = hasImage;
        public ImageData? Image { get; set; }
    }
}