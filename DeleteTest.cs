// using System.Net;
// using Microsoft.Azure.Functions.Worker;
// using Microsoft.Azure.Functions.Worker.Http;
// using Microsoft.Extensions.Logging;

// namespace TS.APITest
// {
//     public class DeleteTest
//     {
//         private readonly ILogger _logger;

//         public DeleteTest(ILoggerFactory loggerFactory)
//         {
//             _logger = loggerFactory.CreateLogger<DeleteTest>();
//         }

//         [Function("DeleteTest")]
//         public HttpResponseData Run([HttpTrigger(AuthorizationLevel.Admin, "get", "post")] HttpRequestData req)
//         {
//             _logger.LogInformation("C# HTTP trigger function processed a request.");

//             var response = req.CreateResponse(HttpStatusCode.OK);
//             response.Headers.Add("Content-Type", "text/plain; charset=utf-8");

//             response.WriteString("Welcome to Azure Functions!");
//             // 为 Azure Cosmos DB 服务创建新的实例
//             CosmosClient cosmosClient = new CosmosClient("AccountEndpoint=https://tsapi-cosmos.documents.azure.com:443/;AccountKey=yaNbhqTNlrzVzzxHU07xuDrfSTybXS3nqbo8c7GbB7UimssEqQuoQLMkuDKYCrmkryxt8JMN6xm3ACDbawcMrg==");

//             // 为数据库和容器创建 CosmosDatabase 和 CosmosContainer 对象
//             CosmosDatabase database = cosmosClient.GetDatabase("TSAPI");
//             CosmosContainer container = database.GetContainer("BankTaiwanSpotRate");

//             // 删除 item
//             string partitionKeyValue = "your partition key value";
//             string itemId = "your item id";
//             ItemResponse<YourItemClass> itemResponse = await container.DeleteItemAsync<YourItemClass>(itemId, new PartitionKey(partitionKeyValue));

//             return response;
//         }
//     }
// }
