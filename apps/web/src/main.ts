import { computed, createApp, onBeforeUnmount, onMounted, ref } from "vue";
import Uppy from "@uppy/core";
import Tus from "@uppy/tus";
import "./style.css";

type Meeting = { id: string; title: string; description?: string; status: string; createdAt: string; roomId?: string };
type Job = { id: string; type?: string; status: string; stage: string; progress: number; errorMessage?: string; error?: string };
type Segment = { id: string; ordinal: number; startMs: number; endMs: number; speaker?: string; speakerLabel?: string; text: string; confidence?: number };
type Media = { id: string; originalName: string; sizeBytes: number; durationMs?: number; status: string; previewStorageKey?: string; archiveStorageKey?: string; asrStorageKey?: string };
type Speaker = { id: string; stableKey: string; displayName: string; confidence?: number };
type Summary = { id?: string; status?: string; content?: { summary?: string; topics?: string[]; risks?: Array<{ text: string }>; open_questions?: Array<{ text: string }>; action_items?: Array<{ task: string; responsible?: string | null; deadline?: string | null }> } };
type Decision = { id: string; text: string; status: string; createdAt?: string };
type Task = { id: string; task: string; responsible?: string | null; deadline?: string | null; status: string; evidenceSegmentId?: string | null };
type Agent = { id: string; name: string; status: string; version?: string; roomName?: string; lastSeenAt?: string };
type AssistantQuery = { id?: string; status?: string; answer?: string; voiceAnswer?: string; errorCode?: string; evidence?: Array<{ segmentId?: string; timecode?: string }> };

type NavItem = { id: string; label: string; icon: string };
const navItems: NavItem[] = [
  { id: "home", label: "Главная", icon: "⌂" },
  { id: "meetings", label: "Совещания", icon: "▤" },
  { id: "tasks", label: "Поручения", icon: "✓" },
  { id: "agents", label: "Источники", icon: "◈" },
  { id: "assistant", label: "Помощник", icon: "✦" },
  { id: "settings", label: "Настройки", icon: "⚙" },
];

const app = {
  setup() {
    const username = ref("admin");
    const password = ref("");
    const authenticated = ref(false);
    const activeNav = ref("home");
    const activeTab = ref("overview");
    const meetings = ref<Meeting[]>([]);
    const selected = ref<Meeting | null>(null);
    const jobs = ref<Job[]>([]);
    const segments = ref<Segment[]>([]);
    const transcriptStatus = ref("PENDING");
    const media = ref<Media[]>([]);
    const speakers = ref<Speaker[]>([]);
    const summary = ref<Summary | null>(null);
    const decisions = ref<Decision[]>([]);
    const tasks = ref<Task[]>([]);
    const agents = ref<Agent[]>([]);
    const taskFilter = ref("ALL");
    const taskSearch = ref("");
    const title = ref("");
    const description = ref("");
    const file = ref<File | null>(null);
    const assistantQuestion = ref("");
    const assistantAnswer = ref<AssistantQuery | null>(null);
    const assistantPrompts = ["Какие поручения остались без срока?", "Какие решения приняли на встрече?", "Что требует внимания?"];
    const busy = ref(false);
    const error = ref("");
    const notice = ref("");
    const uppy = new Uppy({ autoProceed: false }).use(Tus, { endpoint: "/files/", chunkSize: 16 * 1024 * 1024, retryDelays: [0, 1000, 3000, 5000] });
    let eventSources: EventSource[] = [];
    let assistantPollGeneration = 0;

    async function api(path: string, options: RequestInit = {}) {
      const response = await fetch(path, { credentials: "include", ...options });
      if (!response.ok) {
        let message = response.statusText;
        try { const payload = await response.json(); message = payload.error || payload.detail || message; } catch { /* safe fallback */ }
        throw new Error(message);
      }
      return response.status === 204 ? null : response.json();
    }
    function jsonOptions(method: string, body: unknown): RequestInit { return { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) }; }
    function clearMessage() { error.value = ""; notice.value = ""; }

    async function login() {
      clearMessage();
      try { await api("/api/auth/login", jsonOptions("POST", { username: username.value, password: password.value })); authenticated.value = true; await loadMeetings(); await loadAgents(); }
      catch { error.value = "Не удалось войти. Проверьте логин и пароль."; }
    }
    async function logout() { await api("/api/auth/logout", { method: "POST" }).catch(() => undefined); closeEvents(); authenticated.value = false; selected.value = null; meetings.value = []; }
    async function loadMeetings() { meetings.value = await api("/api/meetings"); if (!selected.value && meetings.value.length) await selectMeeting(meetings.value[0]); }
    async function loadAgents() { agents.value = await api("/api/agents").catch(() => []); }
    async function createMeeting() {
      if (!title.value.trim()) return;
      clearMessage();
      try { const meeting = await api("/api/meetings", jsonOptions("POST", { title: title.value.trim(), description: description.value || null })); title.value = ""; description.value = ""; await loadMeetings(); await selectMeeting(meeting); activeNav.value = "meetings"; }
      catch (exc) { error.value = exc instanceof Error ? exc.message : "Не удалось создать совещание"; }
    }
    function closeEvents() { eventSources.forEach((source) => source.close()); eventSources = []; }
    async function refreshSelected() {
      if (!selected.value) return;
      const id = selected.value.id;
      const [jobResult, transcriptResult, mediaResult, speakerResult, summaryResult, decisionResult, taskResult] = await Promise.allSettled([
        api(`/api/meetings/${id}/jobs`), api(`/api/meetings/${id}/transcript`), api(`/api/meetings/${id}/media`), api(`/api/meetings/${id}/speakers`), api(`/api/meetings/${id}/summary`), api(`/api/meetings/${id}/decisions`), api(`/api/meetings/${id}/tasks`),
      ]);
      if (jobResult.status === "fulfilled") jobs.value = jobResult.value || [];
      if (transcriptResult.status === "fulfilled") { transcriptStatus.value = transcriptResult.value.status || "PENDING"; segments.value = transcriptResult.value.segments || []; }
      if (mediaResult.status === "fulfilled") media.value = mediaResult.value || [];
      if (speakerResult.status === "fulfilled") speakers.value = speakerResult.value || [];
      if (summaryResult.status === "fulfilled") summary.value = summaryResult.value;
      if (decisionResult.status === "fulfilled") decisions.value = decisionResult.value || [];
      if (taskResult.status === "fulfilled") tasks.value = taskResult.value || [];
      subscribeJobs();
    }
    function subscribeJobs() {
      closeEvents();
      jobs.value.forEach((job) => {
        const source = new EventSource(`/api/jobs/${job.id}/events`);
        source.addEventListener("progress", async (event) => {
          const update = JSON.parse((event as MessageEvent).data) as Job;
          const index = jobs.value.findIndex((item) => item.id === update.id);
          if (index >= 0) jobs.value[index] = update; else jobs.value.push(update);
          if (["READY", "FAILED"].includes(update.status)) await refreshSelected();
        });
        source.onerror = () => { source.close(); window.setTimeout(() => { if (selected.value) refreshSelected().catch(() => undefined); }, 3000); };
        eventSources.push(source);
      });
    }
    async function selectMeeting(meeting: Meeting) { selected.value = meeting; activeTab.value = "overview"; clearMessage(); await refreshSelected(); }
    async function retryJob(job: Job) { await api(`/api/jobs/${job.id}/retry`, { method: "POST" }); notice.value = "Задание поставлено на повторную обработку"; await refreshSelected(); }
    function chooseFile(event: Event) { file.value = (event.target as HTMLInputElement).files?.[0] || null; }
    async function upload() {
      if (!selected.value || !file.value) return;
      clearMessage(); busy.value = true;
      try {
        const reservation = await api(`/api/meetings/${selected.value.id}/uploads`, jsonOptions("POST", { fileName: file.value.name, sizeBytes: file.value.size }));
        uppy.cancelAll(); uppy.reset();
        const fileId = uppy.addFile({ name: file.value.name, type: file.value.type || "application/octet-stream", data: file.value });
        uppy.setFileMeta(fileId, { reservationId: reservation.uploadId, filename: file.value.name, filetype: file.value.type || "" });
        const result = await uppy.upload();
        if (!result?.successful?.length) throw new Error("Загрузка не завершена");
        file.value = null; notice.value = "Файл принят. Обработка запущена автоматически.";
        for (let i = 0; i < 15 && !jobs.value.length; i++) { await new Promise((resolve) => setTimeout(resolve, 2000)); await refreshSelected(); }
      } catch (exc) { error.value = exc instanceof Error ? exc.message : "Ошибка загрузки"; } finally { busy.value = false; }
    }
    function onlineAgent() { return agents.value.find((agent) => agent.status === "ONLINE") || agents.value[0]; }
    async function recordingCommand(type: string) {
      if (!selected.value) return;
      if (type === "STOP" && !window.confirm("Завершить запись? Agent попросит подтверждение локально.")) return;
      const agent = onlineAgent();
      if (!agent) { error.value = "Нет зарегистрированного Recorder Agent"; return; }
      try { await api(`/api/meetings/${selected.value.id}/recording-commands`, jsonOptions("POST", { agentId: agent.id, commandType: type, payload: { meetingId: selected.value.id } })); notice.value = type === "START" ? "Команда начала записи отправлена" : "Команда отправлена Agent"; await loadAgents(); }
      catch (exc) { error.value = exc instanceof Error ? exc.message : "Команда не отправлена"; }
    }
    async function rebuildSummary() { if (!selected.value) return; busy.value = true; try { await api(`/api/meetings/${selected.value.id}/summary/rebuild`, { method: "POST" }); notice.value = "Саммари поставлено в очередь"; await refreshSelected(); } catch (exc) { error.value = exc instanceof Error ? exc.message : "Не удалось поставить саммари"; } finally { busy.value = false; } }
    async function updateTask(task: Task) {
      const next = window.prompt("Статус поручения", task.status); if (!next || next === task.status) return;
      await api(`/api/tasks/${task.id}`, jsonOptions("PATCH", { task: task.task, responsible: task.responsible, deadline: task.deadline, status: next })); await refreshSelected();
    }
    async function renameSpeaker(speaker: Speaker) { const name = window.prompt("Имя спикера", speaker.displayName); if (!name?.trim() || !selected.value) return; await api(`/api/meetings/${selected.value.id}/speakers/${speaker.id}`, jsonOptions("PATCH", { displayName: name.trim() })); await refreshSelected(); }
    function seek(segment: Segment) { const audio = document.querySelector<HTMLAudioElement>("#meeting-audio"); if (audio) { audio.currentTime = segment.startMs / 1000; audio.play().catch(() => undefined); } }
    async function askAssistant() {
      const question = assistantQuestion.value.trim();
      if (!question) return;
      const generation = ++assistantPollGeneration;
      clearMessage();
      assistantAnswer.value = null;
      try {
        const accepted = await api('/api/assistant/queries', jsonOptions('POST', { meetingId: selected.value?.id || null, query: question })) as AssistantQuery;
        assistantAnswer.value = accepted;
        if (!accepted?.id) return;
        for (let attempt = 0; attempt < 90; attempt++) {
          await new Promise((resolve) => window.setTimeout(resolve, 2000));
          if (generation !== assistantPollGeneration) return;
          const current = await api('/api/assistant/queries/' + accepted.id) as AssistantQuery;
          if (generation !== assistantPollGeneration) return;
          assistantAnswer.value = current;
          if (['READY', 'FAILED', 'NEEDS_REVIEW'].includes(current.status || '')) return;
        }
      } catch (exc) { error.value = exc instanceof Error ? exc.message : 'Помощник недоступен'; }
    }
    const previewUrl = computed(() => media.value.find((item) => item.previewStorageKey)?.id ? `/api/media/${media.value.find((item) => item.previewStorageKey)?.id}/preview` : "");
    const activeJob = computed(() => jobs.value.find((job) => !["READY", "FAILED"].includes(job.status)) || jobs.value[jobs.value.length - 1]);
    const selectedStatus = computed(() => selected.value?.status || "READY");
    const summaryContent = computed(() => summary.value?.content || {});
    const globalTasks = computed(() => tasks.value.length);
    const filteredTasks = computed(() => {
      const query = taskSearch.value.trim().toLowerCase();
      return tasks.value.filter((task) => {
        const matchesStatus = taskFilter.value === "ALL" || task.status === taskFilter.value;
        const haystack = `${task.task} ${task.responsible || ""}`.toLowerCase();
        return matchesStatus && (!query || haystack.includes(query));
      });
    });
    const timecode = (ms: number) => `${Math.floor(ms / 3600000).toString().padStart(2, "0")}:${Math.floor(ms / 60000 % 60).toString().padStart(2, "0")}:${Math.floor(ms / 1000 % 60).toString().padStart(2, "0")}`;
    onMounted(async () => { try { await api("/api/auth/me"); authenticated.value = true; await loadMeetings(); await loadAgents(); } catch { /* show login */ } });
    onBeforeUnmount(closeEvents);

    return { username, password, authenticated, activeNav, activeTab, navItems, meetings, selected, jobs, segments, transcriptStatus, media, speakers, summary, summaryContent, decisions, tasks, agents, title, description, file, assistantQuestion, assistantAnswer, assistantPrompts, taskFilter, taskSearch, filteredTasks, busy, error, notice, globalTasks, activeJob, selectedStatus, previewUrl, timecode, login, logout, loadMeetings, loadAgents, createMeeting, selectMeeting, refreshSelected, chooseFile, upload, seek, retryJob, recordingCommand, rebuildSummary, updateTask, renameSpeaker, askAssistant };
  },
  template: `
    <main v-if="!authenticated" class="login-screen">
      <section class="login-panel">
        <div class="brand-mark">A</div>
        <p class="eyebrow">ЛОКАЛЬНЫЙ КОНТУР ВСТРЕЧ</p>
        <h1>WhisperX <span>Atom</span></h1>
        <p class="muted">Запись, стенограммы и решения совещаний на вашем сервере.</p>
        <input v-model="username" placeholder="Пользователь" autocomplete="username" />
        <input v-model="password" type="password" placeholder="Пароль" autocomplete="current-password" @keyup.enter="login" />
        <button class="primary wide" @click="login">Войти в рабочее место</button>
        <p v-if="error" class="error">{{ error }}</p>
      </section>
    </main>
    <main v-else class="app-shell">
      <aside class="sidebar">
        <div class="brand"><div class="brand-mark small">A</div><div><strong>WhisperX <span>Atom</span></strong><small>локальный сервер</small></div></div>
        <div class="sidebar-section-label">Рабочее место</div>
        <nav>
          <button v-for="item in navItems" :key="item.id" :class="{ active: activeNav === item.id }" @click="activeNav = item.id">
            <i aria-hidden="true">{{ item.icon }}</i><span>{{ item.label }}</span><b v-if="item.id === 'tasks' && globalTasks">{{ globalTasks }}</b>
          </button>
        </nav>
        <div class="sidebar-bottom">
          <div class="server-chip"><span class="dot"></span><div><strong>Сервер онлайн</strong><small>GPU · RTX 5060 Ti</small></div></div>
          <button class="ghost" @click="logout">Выйти</button>
        </div>
      </aside>
      <section class="main-area">
        <header class="topbar">
          <div><p class="eyebrow">{{ activeNav === 'home' ? 'ОБЗОР' : navItems.find((item) => item.id === activeNav)?.label.toUpperCase() }}</p><h2>{{ selected?.title || 'Рабочее место' }}</h2></div>
          <div class="top-actions"><button class="icon-btn" title="Обновить" @click="loadMeetings">↻</button><button class="profile">{{ username.slice(0, 1).toUpperCase() }}</button></div>
        </header>
        <div v-if="error" class="alert error">{{ error }} <button @click="error = ''">×</button></div>
        <div v-if="notice" class="alert success">{{ notice }} <button @click="notice = ''">×</button></div>

        <template v-if="activeNav === 'home'">
          <section class="home-hero">
            <div><p class="eyebrow">РАБОЧИЙ КОНТУР</p><h1>Контролируйте каждое совещание</h1><p class="muted">Начните запись, следите за обработкой и возвращайтесь к важным решениям в одном спокойном рабочем пространстве.</p></div>
            <div class="hero-actions"><button class="primary" @click="selected ? recordingCommand('START') : activeNav = 'meetings'">● Начать запись</button><button class="secondary" @click="activeNav = 'meetings'">Открыть совещания</button></div>
          </section>
          <div class="home-status-strip"><span><i class="dot"></i> Контур готов</span><span>{{ meetings.length }} совещаний в истории</span><span>{{ agents.filter((agent) => agent.status === 'ONLINE').length }} источника онлайн</span><span>{{ jobs.filter((job) => job.status === 'RUNNING' || job.status === 'QUEUED').length }} в обработке</span></div>
          <div class="home-grid">
            <section class="panel meetings-panel"><div class="section-heading"><div><p class="eyebrow">ПОСЛЕДНИЕ ЗАПИСИ</p><h2>Совещания</h2></div><button class="secondary" @click="activeNav = 'meetings'">Все совещания</button></div><div class="meeting-list"><button v-for="meeting in meetings.slice(0, 5)" :key="meeting.id" class="meeting-row" :class="{ selected: selected?.id === meeting.id }" @click="selectMeeting(meeting); activeNav = 'meetings'"><span class="meeting-icon">◷</span><span><strong>{{ meeting.title }}</strong><small>{{ new Date(meeting.createdAt).toLocaleString() }}</small></span><em>{{ meeting.status }}</em></button><div v-if="!meetings.length" class="empty-state">Создайте первое совещание или загрузите запись.</div></div><div class="create-inline"><input v-model="title" placeholder="Название нового совещания" @keyup.enter="createMeeting" /><button class="primary" @click="createMeeting">＋ Создать</button></div></section>
            <aside class="home-rail"><section class="panel quick-panel"><p class="eyebrow">БЫСТРЫЙ СТАРТ</p><h2>Запишите разговор</h2><p>Управляйте Recorder Agent из браузера. Запись продолжится даже при временной потере связи.</p><button class="primary wide" @click="selected ? recordingCommand('START') : activeNav = 'meetings'">● Начать запись</button><button class="secondary wide" @click="activeNav = 'agents'">Настроить источник</button></section><section class="panel status-panel"><div class="section-heading"><div><p class="eyebrow">СИСТЕМА</p><h3>Состояние контура</h3></div><span class="status-pill">ONLINE</span></div><div class="status-line"><span>Recorder Agent</span><strong>{{ agents.filter((agent) => agent.status === 'ONLINE').length ? 'Готов' : 'Не подключён' }}</strong></div><div class="status-line"><span>Обработка</span><strong>{{ jobs.length ? 'Активна' : 'Ожидание' }}</strong></div><button class="link-button" @click="activeNav = 'agents'">Открыть источники →</button></section></aside>
          </div>
        </template>

        <template v-else-if="activeNav === 'meetings'">
          <section class="section-heading page-heading"><div><p class="eyebrow">MEETING CONTROL ROOM</p><h1>Совещания</h1><p class="muted">Создавайте встречи, импортируйте записи и переходите к результатам обработки.</p></div><button class="primary" @click="activeNav = 'meetings'">＋ Новое совещание</button></section>
          <div class="meetings-layout"><section class="panel meeting-browser"><div class="browser-toolbar"><div><h3>История записей</h3><span class="muted">{{ meetings.length }} встреч</span></div><button class="ghost" @click="loadMeetings">Обновить</button></div><div class="create-inline"><input id="new-meeting-title" v-model="title" placeholder="Название нового совещания" @keyup.enter="createMeeting" /><button class="primary" @click="createMeeting">Создать</button></div><div class="meeting-list"><button v-for="meeting in meetings" :key="meeting.id" class="meeting-row" :class="{ selected: selected?.id === meeting.id }" @click="selectMeeting(meeting)"><span class="meeting-icon">◷</span><span><strong>{{ meeting.title }}</strong><small>{{ new Date(meeting.createdAt).toLocaleString() }}</small></span><em>{{ meeting.status }}</em></button><div v-if="!meetings.length" class="empty-state">Пока нет совещаний.</div></div></section>
            <section v-if="selected" class="panel meeting-workspace"><div class="workspace-header"><div><p class="eyebrow">SELECTED MEETING</p><h1>{{ selected.title }}</h1><div class="meeting-meta"><span class="status-pill">{{ selected.status }}</span><span>{{ transcriptStatus }}</span><span>{{ media.length }} файла</span></div></div><div class="recording-actions"><button class="primary" @click="recordingCommand('START')">● Запись</button><button class="secondary" @click="recordingCommand('PAUSE')">Ⅱ Пауза</button><button class="danger" @click="recordingCommand('STOP')">■ Стоп</button></div></div><div class="meeting-body"><div class="meeting-main"><div class="player-card"><div class="player-heading"><span>Аудиозапись</span><span v-if="previewUrl" class="status-pill">PREVIEW</span></div><audio id="meeting-audio" :src="previewUrl" controls></audio><div class="waveform"><i v-for="n in 52" :key="n" :style="{ height: (18 + ((n * 17) % 42)) + 'px' }"></i></div><small v-if="!previewUrl">Preview появится после нормализации медиа.</small></div><div class="processing-strip"><div><span class="eyebrow">ТЕКУЩАЯ ОБРАБОТКА</span><strong>{{ activeJob?.stage || 'Ожидание входящей записи' }}</strong></div><progress :value="activeJob?.progress || 0" max="100"></progress><span>{{ activeJob?.progress || 0 }}%</span></div><div class="tabbar"><button v-for="tab in [{id:'overview',label:'Обзор'},{id:'transcript',label:'Стенограмма'},{id:'summary',label:'Саммари'},{id:'decisions',label:'Решения'},{id:'tasks',label:'Поручения'},{id:'files',label:'Файлы'}]" :key="tab.id" :class="{ active: activeTab === tab.id }" @click="activeTab = tab.id">{{ tab.label }}</button></div><div v-if="activeTab === 'overview'" class="overview-grid"><div><h3>Состояние обработки</h3><div v-for="job in jobs" :key="job.id" class="job-row"><span>{{ job.type || 'JOB' }} · {{ job.stage }}</span><progress :value="job.progress" max="100"></progress><b>{{ job.status }}</b><button v-if="job.status === 'FAILED'" class="link-button" @click="retryJob(job)">Повторить</button></div><div v-if="!jobs.length" class="empty-state">Запись ещё не загружена.</div></div><div class="upload-box"><h3>Добавить запись</h3><p>Аудио или видео → FFmpeg → WhisperX → Qwen.</p><input type="file" accept="audio/*,video/*,.flac,.wav,.m4a,.mp4,.mkv,.mov,.webm" @change="chooseFile" /><button class="primary wide" :disabled="!file || busy" @click="upload">{{ busy ? 'Загрузка…' : 'Загрузить и обработать' }}</button></div></div><div v-else-if="activeTab === 'transcript'" class="transcript-panel"><div class="transcript-toolbar"><div><strong>Стенограмма</strong><span>{{ segments.length }} сегментов · {{ transcriptStatus }}</span></div><button class="secondary" @click="refreshSelected">Обновить</button></div><div class="transcript-layout"><div class="transcript-content"><button v-for="segment in segments" :key="segment.id" class="segment-row" @click="seek(segment)"><span class="timecode">{{ timecode(segment.startMs) }}</span><span class="speaker-label">{{ segment.speaker || segment.speakerLabel || 'Спикер N' }}</span><span class="segment-text">{{ segment.text }}</span><small>{{ segment.confidence ? Math.round(segment.confidence * 100) + '%' : '' }}</small></button><div v-if="!segments.length" class="empty-state">Стенограмма появится после этапов ASR, alignment и diarization.</div></div><aside class="speaker-drawer"><div class="section-heading"><div><p class="eyebrow">УЧАСТНИКИ</p><h3>Спикеры</h3></div><span>{{ speakers.length }}</span></div><div v-for="speaker in speakers" :key="speaker.id" class="speaker-row"><div><strong>{{ speaker.displayName || 'Спикер N' }}</strong><small>{{ speaker.stableKey }} · {{ speaker.confidence ? Math.round(speaker.confidence * 100) + '%' : 'требует проверки' }}</small></div><button class="ghost" @click="renameSpeaker(speaker)">Изменить</button></div><div v-if="!speakers.length" class="empty-state">Появятся после диаризации.</div></aside></div></div><div v-else-if="activeTab === 'summary'" class="summary-panel"><div class="section-heading"><div><p class="eyebrow">AI OUTPUT</p><h3>Итог совещания</h3></div><button class="secondary" @click="rebuildSummary">Пересобрать</button></div><p v-if="summaryContent.summary" class="summary-text">{{ summaryContent.summary }}</p><div v-else class="empty-state">Саммари будет создано автоматически после стенограммы.</div><div class="tag-list"><span v-for="topic in (summaryContent.topics || [])" :key="topic">{{ topic }}</span></div><h3>Риски и открытые вопросы</h3><ul><li v-for="item in [...(summaryContent.risks || []), ...(summaryContent.open_questions || [])]" :key="item.text">{{ item.text }}</li></ul></div><div v-else-if="activeTab === 'decisions'" class="decision-list"><div v-for="decision in decisions" :key="decision.id" class="decision-row"><span class="decision-icon">✓</span><span>{{ decision.text }}</span><small>{{ decision.status }}</small></div><div v-if="!decisions.length" class="empty-state">Решения появятся в саммари.</div></div><div v-else-if="activeTab === 'tasks'" class="task-table"><div class="table-head"><span>Поручение</span><span>Ответственный</span><span>Срок</span><span>Статус</span></div><button v-for="task in tasks" :key="task.id" class="task-row" @click="updateTask(task)"><span>{{ task.task }}</span><span>{{ task.responsible || 'Не назначен' }}</span><span>{{ task.deadline ? new Date(task.deadline).toLocaleDateString() : 'Без срока' }}</span><span class="status-pill">{{ task.status }}</span></button><div v-if="!tasks.length" class="empty-state">Поручения появятся после автоматического саммари.</div></div><div v-else class="file-list"><div v-for="asset in media" :key="asset.id"><strong>{{ asset.originalName }}</strong><small>{{ asset.status }} · {{ Math.round(asset.sizeBytes / 1024 / 1024 * 10) / 10 }} MB</small></div><div v-if="!media.length" class="empty-state">Файлы появятся после загрузки записи.</div></div></div><aside class="meeting-rail"><section class="rail-section"><p class="eyebrow">КОНТЕКСТ</p><h3>Совещание</h3><div class="rail-stat"><span>Статус</span><strong>{{ selected.status }}</strong></div><div class="rail-stat"><span>Сегменты</span><strong>{{ segments.length }}</strong></div><div class="rail-stat"><span>Файлы</span><strong>{{ media.length }}</strong></div></section><section class="rail-section"><p class="eyebrow">ОБРАБОТКА</p><div v-for="job in jobs" :key="'rail-' + job.id" class="rail-job"><span class="dot" :class="job.status === 'FAILED' ? 'off' : ''"></span><div><strong>{{ job.stage }}</strong><small>{{ job.status }} · {{ job.progress }}%</small></div></div><div v-if="!jobs.length" class="empty-state">Pipeline появится после загрузки.</div></section><section class="rail-section"><p class="eyebrow">ПОМОЩНИК</p><p class="muted">Задайте вопрос по текущему совещанию и получите ответ с источниками.</p><button class="secondary wide" @click="activeNav = 'assistant'">Спросить помощника</button></section></aside></div></section><section v-else class="panel empty-meeting"><div class="empty-state">Выберите совещание слева, чтобы открыть его рабочее пространство.</div></section>
          </div>
        </template>

        <template v-else-if="activeNav === 'tasks'">
          <section class="panel page-panel tasks-page"><div class="section-heading page-heading"><div><p class="eyebrow">ACTION REGISTER</p><h1>Поручения</h1><p class="muted">Задачи выбранного совещания с быстрым поиском и фильтром по статусу.</p></div><div class="task-summary"><span class="status-pill">Всего {{ tasks.length }}</span><span class="muted">Показано {{ filteredTasks.length }}</span></div></div><div class="task-toolbar"><input v-model="taskSearch" placeholder="Найти поручение или ответственного" /><select v-model="taskFilter"><option value="ALL">Все статусы</option><option value="OPEN">Открытые</option><option value="NEEDS_REVIEW">На проверке</option><option value="DONE">Готовые</option><option value="CANCELLED">Отменённые</option></select></div><div class="task-table"><div class="table-head"><span>Поручение</span><span>Ответственный</span><span>Срок</span><span>Статус</span></div><button v-for="task in filteredTasks" :key="task.id" class="task-row" @click="updateTask(task)"><span>{{ task.task }}</span><span>{{ task.responsible || 'Не назначен' }}</span><span>{{ task.deadline ? new Date(task.deadline).toLocaleDateString() : 'Без срока' }}</span><span class="status-pill">{{ task.status }}</span></button><div v-if="!filteredTasks.length" class="empty-state">Нет поручений по текущему фильтру.</div></div></section>
        </template>
        <template v-else-if="activeNav === 'agents'"><section class="panel page-panel"><div class="section-heading"><div><p class="eyebrow">RECORDER SOURCES</p><h1>Источники записи</h1><p class="muted">Recorder Agent, переговорные и состояние подключения.</p></div><button class="secondary" @click="loadAgents">Обновить</button></div><div class="agent-grid"><article v-for="agent in agents" :key="agent.id" class="agent-card"><div class="agent-state"><span class="dot" :class="agent.status === 'ONLINE' ? '' : 'off'"></span>{{ agent.status }}</div><h3>{{ agent.name }}</h3><p>{{ agent.roomName || 'Переговорная не назначена' }}</p><small>{{ agent.version || 'версия не указана' }} · {{ agent.lastSeenAt ? new Date(agent.lastSeenAt).toLocaleString() : 'нет heartbeat' }}</small></article><div v-if="!agents.length" class="empty-state">Агенты появятся после регистрации Windows Recorder Agent.</div></div></section></template>
        <template v-else-if="activeNav === 'assistant'">
          <section class="assistant-page panel page-panel"><div class="assistant-header"><div><p class="eyebrow">ATOM ASSISTANT</p><h1>Помощник по истории</h1><p class="muted">{{ selected ? 'Вопрос будет задан в контексте выбранного совещания.' : 'Задайте вопрос по сохранённым совещаниям.' }}</p></div><span class="ai-badge">локальная Qwen</span></div><div v-if="selected" class="assistant-context"><span class="context-dot"></span><div><strong>{{ selected.title }}</strong><small>Контекст текущего совещания</small></div><button class="ghost" @click="activeNav = 'meetings'">Открыть</button></div><div class="assistant-input"><input v-model="assistantQuestion" placeholder="Например: какие поручения остались без срока?" @keyup.enter="askAssistant" /><button class="primary" @click="askAssistant">Спросить</button></div><div class="prompt-list"><button v-for="prompt in assistantPrompts" :key="prompt" class="prompt-chip" @click="assistantQuestion = prompt">{{ prompt }}</button></div><div v-if="assistantAnswer" class="answer"><div class="answer-status"><span class="status-pill" :class="assistantAnswer.status?.toLowerCase()">{{ assistantAnswer.status || 'QUEUED' }}</span></div><p>{{ assistantAnswer.answer || (assistantAnswer.status === 'QUEUED' || assistantAnswer.status === 'RUNNING' ? 'Ожидаю ответ модели…' : assistantAnswer.errorCode || 'Ответ не найден') }}</p><div v-if="assistantAnswer.evidence?.length" class="evidence"><span v-for="item in assistantAnswer.evidence" :key="item.segmentId">Источник · {{ item.timecode || item.segmentId }}</span></div></div><div v-else class="assistant-hint"><span>✦</span><p>Помощник отвечает по сохранённым стенограммам, саммари и поручениям. Каждый ответ возвращает ссылку на источник.</p></div></section>
        </template>
        <template v-else><section class="settings-page"><div class="section-heading page-heading"><div><p class="eyebrow">WORKSPACE SETTINGS</p><h1>Настройки</h1><p class="muted">Основные параметры локального рабочего места и расширенная диагностика.</p></div></div><div class="settings-grid"><section class="panel setting-card"><p class="eyebrow">BACKEND</p><h3>Локальный сервер</h3><p class="muted">API и обработка работают в вашем контуре. Данные не покидают сервер.</p><span class="status-pill">ONLINE</span></section><section class="panel setting-card"><p class="eyebrow">RECORDER</p><h3>Источники записи</h3><p class="muted">Подключено источников: {{ agents.length }} · онлайн: {{ agents.filter((agent) => agent.status === 'ONLINE').length }}</p><button class="secondary" @click="activeNav = 'agents'">Открыть источники</button></section><section class="panel setting-card"><p class="eyebrow">ASSISTANT</p><h3>Локальная Qwen</h3><p class="muted">Помощник отвечает по сохранённым материалам и возвращает evidence.</p><button class="secondary" @click="activeNav = 'assistant'">Открыть помощника</button></section></div><section class="panel advanced-card"><div class="section-heading"><div><p class="eyebrow">ADVANCED</p><h3>Инженерный режим</h3></div><span class="status-pill needs_review">Только для диагностики</span></div><p class="muted">Подключение backend, Docker/WSL2 и регистрация Agent остаются отдельными инженерными действиями desktop-приложения.</p></section></section></template>
      </section>
    </main>`
};

createApp(app).mount("#app");
