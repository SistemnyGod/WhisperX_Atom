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

& docker run --rm --entrypoint python $Image -c $probe
if ($LASTEXITCODE -ne 0) { throw "MEDIA_IMAGE_AUDIO_SIGNAL_FAILED: $Image" }
