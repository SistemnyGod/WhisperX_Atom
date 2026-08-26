# Pinned FFmpeg installer payload

Place the verified Windows x64 `ffmpeg.exe` and `ffprobe.exe` here, or run:

```powershell
.\scripts\stage-ffmpeg-payload.ps1 -SourceDirectory C:\path\to\verified\ffmpeg -Version 7.x.y
```

The staging script writes `ffmpeg-manifest.json` with file sizes and SHA-256
hashes. `scripts/publish-desktop.ps1` copies only this staged payload into the
Recorder Service installer; it never selects an arbitrary executable from the
build host `PATH`.
