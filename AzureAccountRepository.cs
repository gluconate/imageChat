using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace ImageChat;

internal sealed record AccountUpdate<T>(T Result, List<Account>? Users);

internal interface IAccountRepository
{
    T Execute<T>(Func<List<Account>, AccountUpdate<T>> action);
}

internal sealed class AzureAccountRepository(AzureStorage storage) : IAccountRepository
{
    public T Execute<T>(Func<List<Account>, AccountUpdate<T>> action)
    {
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var version = storage.Find("version", "accounts") ?? throw AzureStorage.Unavailable();
                var rows = storage.Rows("a", "accounts");
                var afterRead = storage.Find("version", "accounts") ?? throw AzureStorage.Unavailable();
                if (version.ETag != afterRead.ETag) continue;
                if (version.GetInt32("Count") != rows.Count) throw AzureStorage.Unavailable();
                var users = rows.Select(row => JsonSerializer.Deserialize<Account>(row.GetString("Account")!, UserStore.JsonOptions)
                    ?? throw AzureStorage.Unavailable()).ToList();
                var update = action(users);
                if (update.Users == null) return update.Result;
                version["Revision"] = Guid.NewGuid().ToString("N");
                version["Count"] = update.Users.Count;
                var changes = new List<TableTransactionAction> { AzureStorage.Replace(version) };
                var existing = rows.ToDictionary(row => row.RowKey, StringComparer.Ordinal);
                foreach (var account in update.Users)
                {
                    var key = "a" + account.Id;
                    var json = JsonSerializer.Serialize(account, UserStore.JsonOptions);
                    if (existing.Remove(key, out var row))
                    {
                        if (row.GetString("Account") == json) continue;
                        row["Account"] = json;
                        changes.Add(AzureStorage.Replace(row));
                    }
                    else changes.Add(new(TableTransactionActionType.Add, new TableEntity("accounts", key) { ["Account"] = json }));
                }
                changes.AddRange(existing.Values.Select(row => new TableTransactionAction(TableTransactionActionType.Delete, row, row.ETag)));
                if (changes.Count > 100) throw AzureStorage.Unavailable();
                try
                {
                    storage.Table.SubmitTransaction(changes);
                    return update.Result;
                }
                catch (RequestFailedException error) when (error.Status is 409 or 412) { }
            }
        }
        catch (RequestFailedException) { throw AzureStorage.Unavailable(); }
        throw new AppException(409, "Account information changed concurrently. Please retry.");
    }
}