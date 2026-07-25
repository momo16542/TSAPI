# 建置階段
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY BotRateJob.csproj .
RUN dotnet restore -r linux-x64

COPY . .
# self-contained 發佈：不依賴基底映像的 .NET 版本，Playwright 映像日後升版也不會壞
RUN dotnet publish -c Release -r linux-x64 --self-contained true --no-restore -o /app

# 執行階段：Playwright 官方映像，已含 Chromium 與所有系統相依套件
FROM mcr.microsoft.com/playwright/dotnet:v1.61.0-noble
WORKDIR /app
COPY --from=build /app .

# Playwright 映像已預先安裝瀏覽器，不需要在執行期再下載
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1

ENTRYPOINT ["./BotRateJob"]
