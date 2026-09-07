using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;

namespace ImageChat;

public sealed partial class AzureChatStore
{
    internal static readonly TimeSpan JobVisibility = TimeSpan.FromMinutes(15);

    public Chat QueueTurn(Chat chat, string text, string size, string quality, ImageData? attachment,
        string? telemetryId, GenerationInput input, string? deployment = null)
    {
        var id = StartTurn(chat, text, size, quality, attachment, telemetryId, input.Image == null ? "generation" : "edit", input, input.Model, deployment);
        PublishJob(id);
        return Get(chat.Id);
    }

    private void PublishJob(string id)
    {
        try
        {
            var job = storage.Find("j" + id);
            if (job == null || job.GetString("State") is "completed" or "failed") return;
            storage.Queue.SendMessage(id, timeToLive: TimeSpan.FromDays(7));
            job["PublishedAt"] = DateTimeOffset.UtcNow;
            storage.Table.UpdateEntity(job, job.ETag, TableUpdateMode.Replace);
        }
        catch (RequestFailedException) { }
    }

    public void DispatchPendingJobs()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var job in storage.Rows("j"))
        {
            var state = job.GetString("State");
            if ((state == "queued" || (state == "running" && job.GetDateTimeOffset("StartedAt") < now - JobVisibility)) &&
                (job.GetDateTimeOffset("PublishedAt") == null || job.GetDateTimeOffset("PublishedAt") < now - TimeSpan.FromMinutes(5)))
                PublishJob(job.RowKey[1..]);
        }
        foreach (var chat in storage.Rows("c").Where(row => row.GetBoolean("Deleted") == true))
            CleanupDeletedChat(chat.GetString("Id")!);
        foreach (var row in storage.Rows("", "rate").Where(row => row.GetInt64("Minute") < now.ToUnixTimeSeconds() / 60 - 120))
            storage.Table.DeleteEntity(row.PartitionKey, row.RowKey, row.ETag);
    }

    public async Task<bool> ProcessOneAsync(IImageGenerator generator, CancellationToken cancellationToken)
    {
        var delivery = (await storage.Queue.ReceiveMessageAsync(JobVisibility, cancellationToken)).Value;
        if (delivery == null) return false;
        var id = delivery.Body.ToString();
        if (!Guid.TryParseExact(id, "N", out _))
        {
            await storage.Queue.DeleteMessageAsync(delivery.MessageId, delivery.PopReceipt, cancellationToken);
            return true;
        }
        var job = storage.Find("j" + id);
        if (job != null && job.GetString("State") is not ("completed" or "failed"))
        {
            var chatId = job.GetString("ChatId")!;
            if (job.GetString("State") == "running")
            {
                if (job.GetDateTimeOffset("StartedAt") > DateTimeOffset.UtcNow - JobVisibility)
                    return true;
                var output = storage.Images.GetBlobClient(id);
                if (await output.ExistsAsync(cancellationToken))
                {
                    var recovered = (await output.DownloadContentAsync(cancellationToken)).Value;
                    FinishTurn(chatId, id, new(recovered.Content.ToArray(), recovered.Details.ContentType), null);
                }
                else FinishTurn(chatId, id, null, "The worker was interrupted. The request may have incurred a charge. Review this result before submitting another request.");
            }
            else
            {
                var saved = (await storage.Work.GetBlobClient("input/" + id).DownloadContentAsync(cancellationToken)).Value;
                var input = saved.Content.ToObjectFromJson<GenerationInput>() ?? throw AzureStorage.Unavailable();
                job["State"] = "running";
                job["StartedAt"] = DateTimeOffset.UtcNow;
                try { storage.Table.UpdateEntity(job, job.ETag, TableUpdateMode.Replace); }
                catch (RequestFailedException error) when (error.Status == 412) { return true; }
                ImageData? image = null;
                string? failure = null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(11));
                try { image = await generator.GenerateAsync(input, timeout.Token); }
                catch (Exception error)
                {
                    failure = error is AppException appError ? appError.Message :
                        "Generation failed or was interrupted. Check the saved result before retrying; another request may incur another charge.";
                }
                FinishTurn(chatId, id, image, failure);
            }
        }
        await storage.Work.GetBlobClient("input/" + id).DeleteIfExistsAsync(cancellationToken: cancellationToken);
        await storage.Queue.DeleteMessageAsync(delivery.MessageId, delivery.PopReceipt, cancellationToken);
        return true;
    }

    public void CheckRateLimit(string scope, int limit)
    {
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))) + minute;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var row = storage.Find(key, "rate");
            if ((row?.GetInt32("Count") ?? 0) >= limit) throw new AppException(429, "Too many requests. Wait a minute before trying again.");
            try
            {
                if (row == null) storage.Table.AddEntity(new TableEntity("rate", key) { ["Count"] = 1, ["Minute"] = minute });
                else
                {
                    row["Count"] = row.GetInt32("Count")!.Value + 1;
                    storage.Table.UpdateEntity(row, row.ETag, TableUpdateMode.Replace);
                }
                return;
            }
            catch (RequestFailedException error) when (error.Status is 409 or 412) { }
        }
        throw new AppException(429, "Too many concurrent requests. Please retry later.");
    }
}