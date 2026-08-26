from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_api_download_is_access_checked_and_prefers_permanent_archive():
    api = read("apps/server/WhisperX.Atom.Api/Program.cs")
    route = api[api.index('app.MapGet("/api/media/{id:guid}/download"'):]
    assert "CanAccessMeetingAsync" in route
    assert "media.ArchiveStorageKey ?? media.StorageKey" in route
    assert "enableRangeProcessing: true" in route
    assert "StorageHelpers.StoragePath" in route


def test_desktop_streams_audio_to_atomic_destination_and_exposes_action():
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    contracts = read("apps/desktop/WhisperX.Atom.Desktop/Services/FrontendContracts.cs")
    page = read("apps/desktop/WhisperX.Atom.Desktop/Pages/MeetingsPage.xaml")
    codebehind = read("apps/desktop/WhisperX.Atom.Desktop/Pages/MeetingsPage.xaml.cs")
    assert 'api/media/{mediaId}/download' in client
    assert 'File.Move(temporary, destinationPath, true)' in client
    assert 'Task<bool> DownloadMediaAsync' in contracts
    assert 'Скачать аудио' in page
    assert 'DownloadAudioButton_Click' in codebehind
