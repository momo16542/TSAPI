using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace TS.API
{
    public class GetDateTime
    {
        private readonly ILogger _logger;

        public GetDateTime(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<GetDateTime>();
        }

        [Function("GetDateTime")]
        public HttpResponseData Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
        {
            _logger.LogInformation("GetTime function processed a request.");

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            response.WriteString(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            return response;
        }
    }
}
