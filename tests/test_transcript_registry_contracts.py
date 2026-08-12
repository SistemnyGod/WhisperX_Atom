from pathlib import Path


ROOT = Path(__file__).parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_registry_endpoint_is_paged_lightweight_and_rbac_scoped():
    program = read("apps/server/WhisperX.Atom.Api/Program.cs")
    endpoint = program.split('app.MapGet("/api/transcripts"', 1)[1].split('app.MapGet("/api/search"', 1)[0]
    assert "limit" in endpoint and "offset" in endpoint
    assert "ListTranscriptRegistryAsync" in endpoint
    assert "CurrentUserId(context)" in endpoint
    sql = program.rsplit("public async Task<IReadOnlyList<TranscriptRegistryRow>> ListTranscriptRegistryAsync", 1)[1].split("public async Task<MeetingRow?>", 1)[0]
    assert "(@include_all OR m.owner_id=@owner)" in sql
    assert "COUNT(s.id)" in sql and "MAX(s.end_ms)" in sql
    assert "s.words" not in sql and "s.text" not in sql


def test_registry_filters_are_server_side():
    program = read("apps/server/WhisperX.Atom.Api/Program.cs")
    sql = program.rsplit("public async Task<IReadOnlyList<TranscriptRegistryRow>> ListTranscriptRegistryAsync", 1)[1].split("public async Task<MeetingRow?>", 1)[0]
    for clause in ("@search", "@status", "@date_from", "@date_to"):
        assert clause in sql


def test_desktop_load_uses_one_registry_request_not_n_detail_requests():
    vm = read("apps/desktop/WhisperX.Atom.Desktop/ViewModels/TranscriptsViewModel.cs")
    load = vm.split("public async Task LoadAsync", 1)[1].split("public async Task LoadSelectedAsync", 1)[0]
    assert "GetTranscriptRegistryPageAsync" in load
    assert "GetTranscriptAsync" not in load
    detail = vm.split("public async Task LoadSelectedAsync", 1)[1]
    assert detail.count("GetTranscriptAsync") == 1


def test_client_exposes_a_paged_registry_request():
    client = read("apps/desktop/WhisperX.Atom.Desktop/ServerApiClient.cs")
    method = client.split("GetTranscriptRegistryPageAsync", 1)[1].split("GetCurrentUserAsync", 1)[0]
    assert "api/transcripts?" in method
    assert "limit" in method and "offset" in method
