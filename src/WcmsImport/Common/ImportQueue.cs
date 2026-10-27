using StackExchange.Redis;

namespace WcmsImport.Common;

/// <summary>The Redis Stream that hands each job to one importer.</summary>
public static class ImportQueue
{
    public const string Stream = "import-jobs";
    public const string Group = "importers";
    public const string JobIdField = "jobId";

    /// <summary>Create the consumer group once. Both the API and the importer call this, so start order does not matter.</summary>
    public static async Task EnsureGroupAsync(IDatabase redis)
    {
        try
        {
            await redis.StreamCreateConsumerGroupAsync(Stream, Group, StreamPosition.Beginning, createStream: true);
        }
        catch (RedisServerException e) when (e.Message.Contains("BUSYGROUP"))
        {
            // already exists
        }
    }
}
