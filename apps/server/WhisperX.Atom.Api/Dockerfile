FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY WhisperX.Atom.Api.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG WHISPERX_RELEASE_VERSION=dev
ARG WHISPERX_BUILD_IDENTITY=dev
ARG WHISPERX_REVISION=dev
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
LABEL org.opencontainers.image.title="WhisperX Atom API" \
      org.opencontainers.image.version="$WHISPERX_RELEASE_VERSION" \
      org.opencontainers.image.revision="$WHISPERX_REVISION" \
      io.whisperx.atom.build-identity="$WHISPERX_BUILD_IDENTITY" \
      org.opencontainers.image.source="WhisperX Atom"
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "WhisperX.Atom.Api.dll"]


HEALTHCHECK --interval=10s --timeout=5s CMD curl -fsS http://127.0.0.1:8080/ready || exit 1



