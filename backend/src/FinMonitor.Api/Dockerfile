# Build context: backend/src
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

COPY FinMonitor.Api/FinMonitor.Api.csproj FinMonitor.Api/
RUN dotnet restore FinMonitor.Api/FinMonitor.Api.csproj

COPY FinMonitor.Api/ FinMonitor.Api/
RUN dotnet publish FinMonitor.Api/FinMonitor.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS final
WORKDIR /app
RUN apk add --no-cache su-exec && addgroup -S appgroup && adduser -S appuser -G appgroup
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app .
COPY FinMonitor.Api/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh
EXPOSE 8080
ENTRYPOINT ["docker-entrypoint.sh"]
