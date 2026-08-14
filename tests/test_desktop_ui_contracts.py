from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DESKTOP = ROOT / "apps" / "desktop" / "WhisperX.Atom.Desktop"


def test_phase_one_shell_prioritizes_recording_and_hides_secondary_routes():
    shell = (DESKTOP / "MainWindow.xaml").read_text(encoding="utf-8")
    assert 'Text="Главная"' in shell
    assert 'Text="Recorder · проверка"' in shell
    assert 'Visibility="Collapsed" Content="Серии оперативок"' in shell
    assert 'Visibility="Collapsed" Content="Поиск"' in shell
    assert 'Text="WhisperX Atom Desktop"' not in shell


def test_recording_primary_actions_and_local_first_copy_are_visible():
    recording = (DESKTOP / "Pages" / "RecordingPage.xaml").read_text(encoding="utf-8")
    assert 'Content="Начать запись"' in recording
    assert 'Content="Завершить"' in recording
    assert 'Text="Локальное сохранение"' in recording
    assert 'Text="Системный звук"' in recording


def test_recording_controls_explain_local_first_flow_and_adapt_to_compact_widths():
    recording = (DESKTOP / "Pages" / "RecordingPage.xaml").read_text(encoding="utf-8")
    codebehind = (DESKTOP / "Pages" / "RecordingPage.xaml.cs").read_text(encoding="utf-8")
    assert 'Text="Управление записью"' in recording
    assert 'ToolTipService.ToolTip="Запустить локальную запись"' in recording
    assert 'Text="ПОЗЖЕ"' in recording
    assert 'e.NewSize.Width < 760 ? Orientation.Vertical : Orientation.Horizontal' in codebehind
    assert 'локальную запись' in codebehind


def test_device_selector_exposes_accessible_name_without_refresh_selection_side_effects():
    selector = (DESKTOP / "Controls" / "DeviceSelector.xaml").read_text(encoding="utf-8")
    assert 'AutomationProperties.Name="{x:Bind Label, Mode=OneWay}"' in selector
    assert 'ToolTipService.ToolTip="{x:Bind HelperText, Mode=OneWay}"' in selector
