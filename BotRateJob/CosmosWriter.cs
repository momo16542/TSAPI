using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace BotRateJob;

public sealed class CosmosWriter
{
    private readonly ILogger _logger;

    public CosmosWriter(ILogger logger) => _logger = logger;

    public async Task WriteAsync(IReadOnlyList<BankTaiwanSpotRate> rates)
    {
        using var client = new CosmosClient(Config.CosmosConnectionString, new CosmosClientOptions
        {
            ApplicationName = "BotRateJob"
        });

        var container = client.GetContainer(Config.CosmosDatabase, Config.CosmosContainer);
        var props = await container.ReadContainerAsync();
        _logger.LogInformation("寫入 {Db}/{Container}，分割索引鍵 {PartitionKey}",
            Config.CosmosDatabase, Config.CosmosContainer, props.Resource.PartitionKeyPath);

        var written = 0;
        foreach (var rate in rates)
        {
            // 用固定 id upsert，job 重跑或重試不會產生重複文件
            var response = await container.UpsertItemAsync(rate);
            written++;
            _logger.LogDebug("{Currency} {Buying}/{Selling} (RU {Charge})",
                rate.Currency, rate.SpotRateBuying, rate.SpotRateSelling, response.RequestCharge);
        }

        _logger.LogInformation("已寫入 {Count} 筆", written);
    }
}
