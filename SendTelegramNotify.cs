using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace TS.API
{
    public class SendTelegramNotify
    {
        private readonly ILogger _logger;

        public SendTelegramNotify(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<SendTelegramNotify>();
        }

        [Function("SendTelegramNotify")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
        {
            var response = req.CreateResponse();
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");

            string message = null;
            string threadId = null;

            // POST：從 JSON body 讀 message / threadId
            if (req.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(req.Body);
                string body = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("message", out var m))
                            message = m.GetString();
                        if (doc.RootElement.TryGetProperty("threadId", out var t))
                            threadId = t.ValueKind == JsonValueKind.Number ? t.GetRawText() : t.GetString();
                    }
                    catch (JsonException)
                    {
                        message = body; // 不是 JSON 就當純文字
                    }
                }
            }

            // 從 query 補沒填的欄位
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            if (string.IsNullOrWhiteSpace(message))
                message = query["message"];
            if (string.IsNullOrWhiteSpace(threadId))
                threadId = query["threadId"];

            if (string.IsNullOrWhiteSpace(message))
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                await response.WriteStringAsync("{\"error\":\"missing 'message'\"}");
                return response;
            }

            var (ok, statusCode, detail) = await TSAPI.TelegramNotify.SendNotify(message, _logger, threadId);

            response.StatusCode = ok ? HttpStatusCode.OK : HttpStatusCode.BadGateway;
            var payload = JsonSerializer.Serialize(new { ok, statusCode, detail });
            await response.WriteStringAsync(payload);
            return response;
        }
    }
}
