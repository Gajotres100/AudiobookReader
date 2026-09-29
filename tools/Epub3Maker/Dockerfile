# epub3maker in a container. Built from the repository root, because it needs the app's Core:
#
#   docker build -f tools/Epub3Maker/Dockerfile -t epub3maker .
#
# Runs `scan` by default over /library into /output, keeping models and progress in /data.
# See tools/Epub3Maker/README.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/AudioBookReader.Core/ src/AudioBookReader.Core/
COPY tools/Epub3Maker/ tools/Epub3Maker/

RUN dotnet publish tools/Epub3Maker/Epub3Maker.csproj -c Release -r linux-x64 --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0

# FFmpeg decodes the audio; libgomp is what Whisper's native library is built against.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg libgomp1 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app /app

ENV EPUB3MAKER_DATA=/data \
    EPUB3MAKER_LIBRARY=/library \
    EPUB3MAKER_OUTPUT=/output

VOLUME ["/data"]

ENTRYPOINT ["dotnet", "/app/epub3maker.dll"]
CMD ["scan"]
