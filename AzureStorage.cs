using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;

namespace ImageChat;

public sealed class AzureStorage
{
    internal TableClient Table { get; }
    internal BlobContainerClient Images { get; }
    internal BlobContainerClient Work { get; }
    internal BlobContainerClient Keys { get; }
    internal QueueClient Queue { get; }

    public AzureStorage(IConfiguration configuration, TokenCredential credential)
    {
        var suffix = configuration["Storage:ResourceSuffix"] ?? "";
        var tableName = "imagechat" + suffix;
        var account = configuration["Storage:AccountName"];
        var connection = configuration["Storage:ConnectionString"];
        var queueOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
        if (!string.IsNullOrWhiteSpace(connection))
        {
            Table = new TableClient(connection, tableName);
            Images = new BlobContainerClient(connection, "images" + suffix);
            Work = new BlobContainerClient(connection, "work" + suffix);
            Keys = new BlobContainerClient(connection, "keys" + suffix);
            Queue = new QueueClient(connection, "generation" + suffix, queueOptions);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(account) || !account.All(char.IsAsciiLetterOrDigit))
                throw new InvalidOperationException("Configure Storage:AccountName for Azure storage.");
            Table = new TableClient(new Uri($"https://{account}.table.core.windows.net"), tableName, credential);
            Images = new BlobContainerClient(new Uri($"https://{account}.blob.core.windows.net/images{suffix}"), credential);
            Work = new BlobContainerClient(new Uri($"https://{account}.blob.core.windows.net/work{suffix}"), credential);
            Keys = new BlobContainerClient(new Uri($"https://{account}.blob.core.windows.net/keys{suffix}"), credential);
            Queue = new QueueClient(new Uri($"https://{account}.queue.core.windows.net/generation{suffix}"), credential, queueOptions);
        }
    }

    public void Initialize()
    {
        Table.CreateIfNotExists();
        Images.CreateIfNotExists();
        Work.CreateIfNotExists();
        Keys.CreateIfNotExists();
        Queue.CreateIfNotExists();
        try { Table.AddEntity(new TableEntity("accounts", "version") { ["Revision"] = Guid.NewGuid().ToString("N"), ["Count"] = 0 }); }
        catch (RequestFailedException error) when (error.Status == 409) { }
    }

    internal TableEntity? Find(string key, string partition = "app")
    {
        var response = Table.GetEntityIfExists<TableEntity>(partition, key);
        return response.HasValue ? response.Value : null;
    }

    internal List<TableEntity> Rows(string prefix, string partition = "app") => Table.Query<TableEntity>(
        TableClient.CreateQueryFilter($"PartitionKey eq {partition} and RowKey ge {prefix} and RowKey lt {prefix + "~"}")).ToList();

    internal static TableTransactionAction Replace(TableEntity entity) =>
        new(TableTransactionActionType.UpdateReplace, entity, entity.ETag);

    internal static AppException Unavailable() => new(503, "Cloud storage is unavailable. Retry the read or check the saved result before submitting again.");
}