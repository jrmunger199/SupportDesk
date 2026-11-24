# Build context: the repository root.
# Base images are pinned by digest so a build cannot silently pick up a different (or tampered) image behind the same
# tag. Dependabot proposes digest updates. The tag is kept for readability.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d AS build
WORKDIR /src

# Restore first so this layer is cached until a project file changes.
COPY global.json Directory.Build.props .editorconfig ./
COPY src/SupportDesk.Domain/SupportDesk.Domain.csproj src/SupportDesk.Domain/
COPY src/SupportDesk.Data/SupportDesk.Data.csproj src/SupportDesk.Data/
COPY src/SupportDesk.Api/SupportDesk.Api.csproj src/SupportDesk.Api/
RUN dotnet restore src/SupportDesk.Api

COPY src/ src/
RUN dotnet publish src/SupportDesk.Api -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0@sha256:ca396ece3f2a2e62c7a03f1d7d710b8bc7e8c9dea5597d091178d9f5bfca13f0 AS runtime
WORKDIR /app
COPY --from=build /app .

# The base image ships a non-root "app" user; never run the service as root.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# The runtime image has no curl or wget, so the app can probe itself: `--healthcheck` calls /health and sets the exit code.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 CMD ["dotnet", "SupportDesk.Api.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "SupportDesk.Api.dll"]
