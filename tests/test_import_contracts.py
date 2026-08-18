from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
API = ROOT / "apps" / "server" / "WhisperX.Atom.Api" / "Program.cs"
DESKTOP = ROOT / "apps" / "desktop" / "WhisperX.Atom.Desktop"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def test_editor_can_create_a_meeting_and_reserve_its_own_upload():
    api = read(API)
    assert 'path == "/api/meetings"' in api
    assert 'path.Value?.EndsWith("/uploads", StringComparison.OrdinalIgnoreCase)' in api


def test_archive_and_original_downloads_have_explicit_variants():
    api = read(API)
    client = read(DESKTOP / "ServerApiClient.cs")
    assert 'requestedVariant is not ("archive" or "original")' in api
    assert 'MediaPolicy.IsVideoExtension(media.OriginalName)' in api
    assert 'var storageKey = original ? media.StorageKey : media.ArchiveStorageKey ?? media.StorageKey;' in api
    assert 'DownloadOriginalMediaAsync' in client
    assert 'download?variant=' in client


def test_import_transfer_coordinates_are_persisted_without_credentials_or_media():
    store = read(DESKTOP / "Services" / "DesktopImportTransferStore.cs")
    client = read(DESKTOP / "ServerApiClient.cs")
    assert 'import-transfers.json' in store
    assert 'cookie, media bytes, transcript text or a server token' in store
    assert '_importTransfers.Find' in client
    assert '_importTransfers.Upsert' in client
    assert 'UploadCompleted = true' in client


def test_meetings_page_shows_upload_progress_and_preserves_background_processing():
    page = read(DESKTOP / "Pages" / "MeetingsPage.xaml")
    codebehind = read(DESKTOP / "Pages" / "MeetingsPage.xaml.cs")
    assert 'x:Name="ImportProgressPanel"' in page
    assert 'x:Name="ImportProgressBar"' in page
    assert 'ImportFileWithProgressAsync' in codebehind
    assert 'Файл принят. Аудио будет подготовлено' in codebehind
    assert 'DownloadOriginalButton_Click' in codebehind


def test_manual_summary_requires_a_quality_approved_enriched_transcript():
    api = read(API)
    store = read(ROOT / "apps" / "server" / "WhisperX.Atom.Api" / "UnifiedProductStore.cs")
    assert 'GetSummaryEligibilityAsync' in api
    assert 'SUMMARY_BLOCKED_BY_TRANSCRIPT_QUALITY' in api
    assert 'versionKind != "ENRICHED"' in store
    assert 'warningSet.Overlaps(blockingWarnings)' in store
