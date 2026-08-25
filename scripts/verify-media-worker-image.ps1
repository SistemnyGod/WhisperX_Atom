[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Image
)

$ErrorActionPreference = 'Stop'

# This probe runs inside the immutable image that will be placed in a release
# bundle. It deliberately generates a tiny synthetic WAV in the container: no
# customer audio, host paths, or diagnostic artifacts cross the boundary.
$probe = @'
import wave
from pathlib import Path
from tempfile import TemporaryDirectory
import numpy
from whisperx_atom.audio_signal import analyze_wav

with TemporaryDirectory() as directory:
    path = Path(directory) / "probe.wav"
    with wave.open(str(path), "wb") as target:
        target.setnchannels(1)
        target.setsampwidth(2)
        target.setframerate(16000)
        # Keep the probe free of nested quotes: PowerShell's native argument
        # marshalling can strip them when passing a Python -c payload on
        # Windows.  The bytes() form is equivalent and remains portable.
        target.writeframes(bytes([1, 0]) * 1600)
    result = analyze_wav(path)
    assert result.sample_rate == 16000
print("MEDIA_IMAGE_AUDIO_SIGNAL_READY")
'@

# Passing a multiline program through `python -c` is not reliable on Windows:
# native argument marshalling removes quotes from the payload.  Mount a
# short-lived source file instead.  It contains no customer data and is
# removed even when the container probe fails.
$probePath = Join-Path ([System.IO.Path]::GetTempPath()) ("whisperx-media-probe-" + [guid]::NewGuid().ToString("N") + ".py")
try {
    Set-Content -LiteralPath $probePath -Value $probe -Encoding utf8 -NoNewline
    & docker run --rm --entrypoint python --mount ("type=bind,source=" + $probePath + ",target=/tmp/verify_media.py,readonly") $Image /tmp/verify_media.py
    if ($LASTEXITCODE -ne 0) { throw "MEDIA_IMAGE_AUDIO_SIGNAL_FAILED: $Image" }
}
finally {
    Remove-Item -LiteralPath $probePath -Force -ErrorAction SilentlyContinue
}
