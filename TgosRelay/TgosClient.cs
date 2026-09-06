namespace TgosRelay;

/// <summary>上游故障（連不上、逾時、非 2xx、回應不是 JSON）。與「查無」分開：查無是正常結果（found=false），故障要回 502 讓 YYAPI 知道「這次不算數」。</summary>
public sealed class TgosUpstreamException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 打 TGOS QueryAddr（POST form）。參數組合與 ERPV2 <c>TgosGeocodeProvider</c> 相同：
/// EPSG:4326、模糊比對、只回 1 筆、可忽略村里／鄰。
/// </summary>
public sealed class TgosClient(HttpClient http, RelayConfig cfg)
{
    public async Task<TgosHit?> QueryAsync(string 地址, CancellationToken ct)
    {
        var 表單 = new Dictionary<string, string>
        {
            ["oAPPId"] = cfg.TgosAppId,
            ["oAPIKey"] = cfg.TgosApiKey,
            ["oAddress"] = 地址,
            ["oSRS"] = "EPSG:4326",
            ["oFuzzyType"] = "2",
            ["oResultDataType"] = "JSON",
            ["oFuzzyBuffer"] = "0",
            ["oIsOnlyFullMatch"] = "false",
            ["oIsLockCounty"] = "false",
            ["oIsLockTown"] = "false",
            ["oIsLockVillage"] = "false",
            ["oIsLockRoadSection"] = "false",
            ["oIsLockLane"] = "false",
            ["oIsLockAlley"] = "false",
            ["oIsLockArea"] = "false",
            ["oIsSameNumber_SubNumber"] = "true",
            ["oCanIgnoreVillage"] = "true",
            ["oCanIgnoreNeighborhood"] = "true",
            ["oReturnMaxCount"] = "1",
        };

        string body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, cfg.TgosUrl) { Content = new FormUrlEncodedContent(表單) };
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new TgosUpstreamException($"TGOS 回 HTTP {(int)resp.StatusCode}：{截短(body)}");
        }
        catch (TgosUpstreamException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TgosUpstreamException("TGOS 逾時");
        }
        catch (HttpRequestException ex)
        {
            throw new TgosUpstreamException($"TGOS 連線失敗：{ex.Message}", ex);
        }

        try
        {
            return TgosResponseParser.Parse(body, cfg.XySwap);
        }
        catch (System.Text.Json.JsonException ex)
        {
            // TGOS 金鑰錯／IP 未登記時常回一段說明文字而不是 JSON——當上游故障處理，訊息帶前 200 字給維運看
            throw new TgosUpstreamException($"TGOS 回應不是 JSON：{截短(body)}", ex);
        }
    }

    private static string 截短(string s) => s.Length <= 200 ? s : s.Substring(0, 200) + "…";
}
