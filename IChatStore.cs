namespace ImageChat;

public interface IChatStore
{
    List<ChatSummary> List(string? ownerId = null);
    Chat Create(string? ownerId = null);
    Chat Get(string id, string? ownerId = null);
    ImageData? GetImage(string id, string? ownerId = null);
    ImageData? LatestImage(Chat chat);
    string BeginTurn(Chat chat, string text, string size, string quality, ImageData? attachment,
        string? telemetryId = null, string operation = "unknown", string? model = null, string? deployment = null);
    void SetRequestModel(string requestId, string model, string deployment);
    void FinishTurn(string chatId, string messageId, ImageData? image, string? error);
    void UpdateTextTurn(string chatId, string messageId, string text);
    void FinishTextTurn(string chatId, string messageId, string text, string? error, int httpStatus = 200);
    void Delete(string id, string? ownerId = null);
    void ClaimLegacyChats(string ownerId);
    string StartRequest(string userId, string username, string? ip);
    void EndRequest(string id, int httpStatus);
    TelemetryReport Report(TelemetryFilter filter);
}