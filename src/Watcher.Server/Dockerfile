# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Directory.Build.props sets <Version> for the whole repo (see Directory.Build.props
# at repo root) — MSBuild walks up from the csproj looking for it, so it must land
# here before restore/publish or the image silently builds with the SDK default
# version (1.0.0) instead of the real one.
COPY Directory.Build.props .
COPY src/Watcher.Server/Watcher.Server.csproj src/Watcher.Server/
RUN dotnet restore src/Watcher.Server/Watcher.Server.csproj
# Общие исходники (src/Shared), подключаются в .csproj ссылкой.
COPY src/Shared/ src/Shared/
COPY src/Watcher.Server/ src/Watcher.Server/
RUN dotnet publish src/Watcher.Server/Watcher.Server.csproj -c Release --no-restore -o /app

# ffmpeg для пережатия записей (Transcode.cs) — закреплённая статическая сборка
# с SVT-AV1 4.x вместо пакета Debian (SVT-AV1 1.4.1 — втрое медленнее).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS ffmpeg
RUN apt-get update && apt-get install -y --no-install-recommends xz-utils && rm -rf /var/lib/apt/lists/*
COPY tools/fetch-ffmpeg-linux.sh /fetch-ffmpeg-linux.sh
RUN /fetch-ffmpeg-linux.sh /ffmpeg

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# A5 (roadmap-next.md): контейнер раньше работал под root без причины — образ
# mcr.microsoft.com/dotnet/aspnet:8.0+ уже содержит непривилегированного
# пользователя "app" специально для этого. /data — точка монтирования
# docker-compose-volume'а (Server__DataDir=/data), её нужно создать и отдать
# "app" здесь: Docker копирует права каталога из образа при первом создании
# named volume.
RUN mkdir -p /data && chown -R app:app /data /app
# H11 (docs/roadmap-next.md): базовый образ Microsoft пересобирается раз в месяц,
# исправления Debian между пересборками в него не попадают — Trivy находил
# CRITICAL в perl-base, исправленные в Debian, но не в образе. Обновляем пакеты
# при каждой сборке; набор пакетов не меняется (только upgrade, без install).
RUN apt-get update \
    && apt-get upgrade -y --no-install-recommends \
    && rm -rf /var/lib/apt/lists/*
COPY --from=ffmpeg /ffmpeg/ffmpeg /usr/local/bin/ffmpeg
USER app
EXPOSE 5050
# H4: /healthz (БД, свободное место, retention). curl в образе нет — проверку
# делает сам сервер (`--healthcheck`, CliCommands.HealthCheck).
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD ["dotnet", "Watcher.Server.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Watcher.Server.dll"]
