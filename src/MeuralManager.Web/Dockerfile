# Build from the repository root:
#   docker build -f src/MeuralManager.Web/Dockerfile -t meuralmanager-web .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/MeuralManager.Core/MeuralManager.Core.csproj src/MeuralManager.Core/
COPY src/MeuralManager.Web/MeuralManager.Web.csproj src/MeuralManager.Web/
RUN dotnet restore src/MeuralManager.Web/MeuralManager.Web.csproj

COPY src/MeuralManager.Core/ src/MeuralManager.Core/
COPY src/MeuralManager.Web/ src/MeuralManager.Web/
# No --no-restore here: the earlier restore only saw the .csproj (for layer caching), which
# leaves a stale static-web-assets manifest that omits Blazor's own framework JS
# (wwwroot/_framework/blazor.web.js) once the real source lands. Restoring again against the
# full source is what regenerates that manifest correctly - it's fast since packages are
# already cached from the first restore.
RUN dotnet publish src/MeuralManager.Web/MeuralManager.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
ENV DATA_PROTECTION_KEYS_PATH=/data/keys
ENV CACHE_ROOT_PATH=/data/cache
EXPOSE 8080

ENTRYPOINT ["dotnet", "MeuralManager.Web.dll"]
