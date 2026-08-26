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


def test_manual_summary_uses_deterministic_mode_for_a_usable_v1_and_full_mode_for_v2():
    api = read(API)
    store = read(ROOT / "apps" / "server" / "WhisperX.Atom.Api" / "UnifiedProductStore.cs")
    assert 'GetSummaryEligibilityAsync' in api
    assert 'SUMMARY_BLOCKED_BY_TRANSCRIPT_QUALITY' in api
    assert 'return new SummaryEligibility(true, true, "DETERMINISTIC_ONLY", "DETERMINISTIC_ONLY")' in store
    assert 'summaryMode = transcriptKind == "ENRICHED" ? "FULL" : "DETERMINISTIC_ONLY"' in store
    assert 'AUDIO_SIGNAL_UNUSABLE' not in store[store.index('public async Task<SummaryEligibility> GetSummaryEligibilityAsync'):]
    eligibility = store[store.index('public async Task<SummaryEligibility> GetSummaryEligibilityAsync'):]
    assert 'warningSet.Contains("NO_SPEECH_DETECTED")' in eligibility
    assert 'versionKind != "ENRICHED"' in eligibility
    assert 'status is not "READY" || warningSet.Contains("ASR_LANGUAGE_MISMATCH")' in eligibility
    assert eligibility.count("CASE WHEN COALESCE(version_kind,'')='ENRICHED'") >= 1
    assert "WHEN COALESCE(version_kind,'')<>'ENRICHED'" in eligibility
    queue = store[store.index('public async Task<Guid?> QueueSummaryAsync'):store.index('RepairMeetingPipelineAsync')]
    assert "CASE WHEN COALESCE(version_kind,'')='ENRICHED'" in queue
    assert "WHEN COALESCE(version_kind,'')<>'ENRICHED'" in queue
    repair = store[store.index('RepairMeetingPipelineAsync'):store.index('public async Task<SummaryEligibility>')]
    assert 'var v1Enrichable = v1Usable && !blockingWarnings.Contains("ASR_LANGUAGE_MISMATCH")' in repair
    assert 'cyrillicRatio >= 0.70' in repair
    assert 'latinRatio < 0.20' in repair
    assert 'var summaryMode = qualifiedV2Id is null ? "DETERMINISTIC_ONLY" : "FULL"' in repair


def test_pipeline_repair_is_privileged_preview_apply_and_does_not_replay_asr():
    api = read(API)
    store = read(ROOT / "apps" / "server" / "WhisperX.Atom.Api" / "UnifiedProductStore.cs")
    desktop = read(DESKTOP / "Pages" / "MeetingsPage.xaml.cs")
    assert 'MapPost("/api/meetings/{id:guid}/pipeline/repair"' in api
    assert 'if (!IsPrivileged(context)) return Results.Forbid();' in api
    assert 'mode is not ("PREVIEW" or "APPLY")' in api
    assert 'RepairMeetingPipelineAsync' in store
    assert 'A usable V1 always receives exactly one deterministic draft' in store
    assert "TRANSCRIBE_ASR','QUEUED" not in store[store.index('RepairMeetingPipelineAsync'):store.index('public async Task<SummaryEligibility>')]
    assert 'RepairMeetingPipelineAsync(meetingId, "PREVIEW"' in desktop
    assert 'RepairMeetingPipelineAsync(meetingId, "APPLY"' in desktop
