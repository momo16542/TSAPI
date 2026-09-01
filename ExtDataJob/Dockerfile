# 外部資料抓取 job 的容器映像。
#
# **刻意不在容器內從原始碼建置**（與 BotRateJob 不同）：
# 本 job 用相對路徑引用另一個 repo（ERPV2）的四個共用專案，而 Docker build context
# 只有 TSAPI 這個資料夾，跨 repo 的原始碼不在裡面，容器內建置一定失敗。
# 使用者裁決 A2（2026-09-01）：先在建置機 dotnet publish，映像只 COPY 產出。
#
# 建置步驟（見 publish.ps1）：
#   1. dotnet publish -c Release -r linux-x64 --self-contained false -o publish
#   2. docker build -t extdatajob .
#
# 不需要 Playwright：本 job 全部走 HttpClient，行情頁是 server-rendered 直接解析，
# 沒有要跑瀏覽器（BotRateJob 抓台銀才需要）。所以用一般 runtime 映像即可，體積小很多。

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

# 中文與 zh-TW 文化：公會資料是中文欄名與中文月份表名
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

COPY publish/ .

ENTRYPOINT ["dotnet", "ExtDataJob.dll"]
