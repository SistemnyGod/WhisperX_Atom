from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

from whisperx_atom.historical_import import classify_duplicates, discover, parse_document


def test_timestamped_txt_preserves_segments_and_does_not_expose_text_in_preview(tmp_path: Path):
    path = tmp_path / "meeting_24.08.2026.txt"
    path.write_text("[00:00:01] Иван: Обсудили ремонт.\n[00:00:04] Решили начать завтра.\n", encoding="utf-8")
    document = parse_document(path)
    assert document.decision == "IMPORT"
    assert document.date_precision == "DAY"
    assert document.timing_quality == "EXACT"
    assert document.segments[0].speaker == "Иван"
    assert document.segments[0].start_ms == 1000
    assert document.segments[0].end_ms == 4000
    assert "text" not in document.preview()
    assert "Обсудили" not in str(document.preview())


def test_docx_without_timestamps_is_importable_with_absent_timing(tmp_path: Path):
    path = tmp_path / "20260824_093000.docx"
    document_xml = """<?xml version='1.0'?><w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>Решили проверить насос.</w:t></w:r></w:p></w:body></w:document>"""
    with ZipFile(path, "w", ZIP_DEFLATED) as archive:
        archive.writestr("word/document.xml", document_xml)
    document = parse_document(path)
    assert document.decision == "IMPORT"
    assert document.timing_quality == "ABSENT"
    assert document.segments[0].start_ms == document.segments[0].end_ms == 0


def test_undated_summary_and_corrupt_docx_are_quarantined(tmp_path: Path):
    undated = tmp_path / "summary.txt"
    undated.write_text("итог встречи", encoding="utf-8")
    corrupt = tmp_path / "meeting_24.08.2026.docx"
    corrupt.write_bytes(b"not a zip")
    docs = discover(tmp_path)
    assert {doc.decision for doc in docs} == {"QUARANTINE"}
    assert all(doc.quarantine_reason for doc in docs)


def test_identical_content_is_excluded_but_similar_same_date_requires_review(tmp_path: Path):
    first = tmp_path / "a_24.08.2026.txt"
    duplicate = tmp_path / "b_24.08.2026.txt"
    similar = tmp_path / "c_24.08.2026.txt"
    first.write_text("[00:00:01] Обсудили ремонт и срок.", encoding="utf-8")
    duplicate.write_text("[00:00:01] Обсудили ремонт и срок.", encoding="utf-8")
    similar.write_text("[00:00:01] Обсудили ремонт и новый срок.", encoding="utf-8")
    docs = discover(tmp_path)
    by_name = {doc.path.name: doc for doc in docs}
    assert by_name["b_24.08.2026.txt"].decision == "EXCLUDE_DUPLICATE"
    assert by_name["c_24.08.2026.txt"].decision == "REVIEW_REQUIRED"

