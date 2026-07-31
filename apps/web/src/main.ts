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

type NavItem = { id: string; label: string; icon: string };
const navItems: NavItem[] = [
  { id: "home", label: "Главная", icon: "⌂" },
  { id: "meetings", label: "Совещания", icon: "▤" },
  { id: "tasks", label: "Поручения", icon: "✓" },
  { id: "agents", label: "Агенты", icon: "◈" },
  { id: "assistant", label: "Помощник", icon: "✦" },
];

const app = {
  setup() {
    const username = ref("admin");
    const password = ref("change-me-now");
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
    const title = ref("");
    const description = ref("");
    const file = ref<File | null>(null);
    const assistantQuestion = ref("");
    const assistantAnswer = ref<{ answer?: string; evidence?: Array<{ segmentId?: string; timecode?: string }> } | null>(null);
    const busy = ref(false);
    const error = ref("");
    const notice = ref("");
    const uppy = new Uppy({ autoProceed: false }).use(Tus, { endpoint: "/files/", chunkSize: 16 * 1024 * 1024, retryDelays: [0, 1000, 3000, 5000] });
    let eventSources: EventSource[] = [];

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
      if (!assistantQuestion.value.trim()) return;
      try { assistantAnswer.value = await api("/api/assistant/queries", jsonOptions("POST", { meetingId: selected.value?.id || null, query: assistantQuestion.value.trim() })); }
      catch (exc) { error.value = exc instanceof Error ? exc.message : "Помощник недоступен"; }
    }
    const previewUrl = computed(() => media.value.find((item) => item.previewStorageKey)?.id ? `/api/media/${media.value.find((item) => item.previewStorageKey)?.id}/preview` : "");
    const activeJob = computed(() => jobs.value.find((job) => !["READY", "FAILED"].includes(job.status)) || jobs.value[jobs.value.length - 1]);
    const selectedStatus = computed(() => selected.value?.status || "READY");
    const summaryContent = computed(() => summary.value?.content || {});
    const globalTasks = computed(() => tasks.value.length);
    const timecode = (ms: number) => `${Math.floor(ms / 3600000).toString().padStart(2, "0")}:${Math.floor(ms / 60000 % 60).toString().padStart(2, "0")}:${Math.floor(ms / 1000 % 60).toString().padStart(2, "0")}`;
    onMounted(async () => { try { await api("/api/auth/me"); authenticated.value = true; await loadMeetings(); await loadAgents(); } catch { /* show login */ } });
    onBeforeUnmount(closeEvents);

    return { username, password, authenticated, activeNav, activeTab, navItems, meetings, selected, jobs, segments, transcriptStatus, media, speakers, summary, summaryContent, decisions, tasks, agents, title, description, file, assistantQuestion, assistantAnswer, busy, error, notice, globalTasks, activeJob, selectedStatus, previewUrl, timecode, login, logout, loadMeetings, loadAgents, createMeeting, selectMeeting, refreshSelected, chooseFile, upload, seek, retryJob, recordingCommand, rebuildSummary, updateTask, renameSpeaker, askAssistant };
  },
  template: `
    <main v-if="!authenticated" class="login-screen">
      <section class="login-panel"><div class="brand-mark">A</div><p class="eyebrow">ЛОКАЛЬНЫЙ КОНТУР ВСТРЕЧ</p><h1>WhisperX <span>Atom</span></h1><p class="muted">Запись, стенограммы и решения совещаний на вашем сервере.</p><input v-model="username" placeholder="Пользователь" autocomplete="username" /><input v-model="password" type="password" placeholder="Пароль" autocomplete="current-password" @keyup.enter="login" /><button class="primary wide" @click="login">Войти в рабочее место</button><p v-if="error" class="error">{{ error }}</p></section>
    </main>
    <main v-else class="app-shell">
      <aside class="sidebar">
        <div class="brand"><div class="brand-mark small">A</div><div><strong>WhisperX <span>Atom</span></strong><small>локальный сервер</small></div></div>
        <nav><button v-for="item in navItems" :key="item.id" :class="{ active: activeNav === item.id }" @click="activeNav = item.id"><i>{{ item.icon }}</i>{{ item.label }}<b v-if="item.id === 'tasks' && globalTasks">{{ globalTasks }}</b></button></nav>
        <div class="sidebar-bottom"><div class="server-chip"><span class="dot"></span><div><strong>Сервер онлайн</strong><small>GPU · RTX 5060 Ti</small></div></div><button class="ghost" @click="logout">Выйти</button></div>
      </aside>
      <section class="main-area">
        <header class="topbar"><div><p class="eyebrow">{{ activeNav === 'home' ? 'ОБЗОР' : navItems.find((item) => item.id === activeNav)?.label.toUpperCase() }}</p><h2>{{ selected?.title || 'Рабочее место' }}</h2></div><div class="top-actions"><button class="icon-btn" title="Обновить" @click="loadMeetings">↻</button><button class="profile">{{ username.slice(0, 1).toUpperCase() }}</button></div></header>
        <div v-if="error" class="alert error">{{ error }} <button @click="error = ''">×</button></div><div v-if="notice" class="alert success">{{ notice }} <button @click="notice = ''">×</button></div>
        <template v-if="activeNav === 'assistant'"><section class="assistant-page panel"><div class="section-heading"><div><p class="eyebrow">ATOM ASSISTANT</p><h1>Спросите по совещаниям</h1></div><span class="ai-badge">локальная Qwen</span></div><div class="assistant-input"><input v-model="assistantQuestion" placeholder="Например: какие поручения остались без срока?" @keyup.enter="askAssistant" /><button class="primary" @click="askAssistant">Спросить</button></div><div v-if="assistantAnswer" class="answer"><p>{{ assistantAnswer.answer || 'Ответ не найден' }}</p><div v-if="assistantAnswer.evidence?.length" class="evidence"><span v-for="item in assistantAnswer.evidence" :key="item.segmentId">Источник · {{ item.timecode || item.segmentId }}</span></div></div><div v-else class="assistant-hint"><span>✦</span><p>Помощник отвечает по сохранённым стенограммам, саммари и поручениям. Каждый ответ возвращает ссылку на источник.</p></div></section></template>
        <template v-else-if="activeNav === 'agents'"><section class="panel"><div class="section-heading"><div><p class="eyebrow">RECORDER AGENTS</p><h1>Источники записи</h1></div><button class="secondary" @click="loadAgents">Обновить</button></div><div class="agent-grid"><article v-for="agent in agents" :key="agent.id" class="agent-card"><div class="agent-state"><span class="dot" :class="agent.status === 'ONLINE' ? '' : 'off'"></span>{{ agent.status }}</div><h3>{{ agent.name }}</h3><p>{{ agent.roomName || 'Переговорная не назначена' }}</p><small>{{ agent.version || 'версия не указана' }} · {{ agent.lastSeenAt ? new Date(agent.lastSeenAt).toLocaleString() : 'нет heartbeat' }}</small></article><div v-if="!agents.length" class="empty-state">Агенты появятся после регистрации Windows Recorder Agent.</div></div></section></template>
        <template v-else-if="activeNav === 'tasks'"><section class="panel"><div class="section-heading"><div><p class="eyebrow">ACTION REGISTER</p><h1>Поручения</h1></div></div><div class="task-table"><div class="table-head"><span>Поручение</span><span>Ответственный</span><span>Срок</span><span>Статус</span></div><button v-for="task in tasks" :key="task.id" class="task-row" @click="updateTask(task)"><span>{{ task.task }}</span><span>{{ task.responsible || 'Не назначен' }}</span><span>{{ task.deadline ? new Date(task.deadline).toLocaleDateString() : 'Без срока' }}</span><span class="status-pill" :class="task.status.toLowerCase()">{{ task.status }}</span></button><div v-if="!tasks.length" class="empty-state">Поручения появятся после обработки саммари выбранного совещания.</div></div></section></template>
        <template v-else><div class="stats"><article><small>Совещаний</small><strong>{{ meetings.length }}</strong><span class="trend">архив и текущие</span></article><article><small>В обработке</small><strong>{{ jobs.filter((job) => job.status === 'RUNNING' || job.status === 'QUEUED').length }}</strong><span class="trend">NATS pipeline</span></article><article><small>Поручений</small><strong>{{ globalTasks }}</strong><span class="trend">требуют контроля</span></article><article><small>Агенты</small><strong>{{ agents.filter((agent) => agent.status === 'ONLINE').length }}</strong><span class="trend">онлайн сейчас</span></article></div><div class="workspace-grid"><section class="panel meetings-panel"><div class="section-heading"><div><p class="eyebrow">MEETINGS</p><h1>Последние совещания</h1></div><button class="secondary" @click="activeNav = 'meetings'">Все совещания</button></div><div class="meeting-list"><button v-for="meeting in meetings.slice(0, 5)" :key="meeting.id" class="meeting-row" :class="{ selected: selected?.id === meeting.id }" @click="selectMeeting(meeting)"><span class="meeting-icon">◷</span><span><strong>{{ meeting.title }}</strong><small>{{ new Date(meeting.createdAt).toLocaleString() }}</small></span><em>{{ meeting.status }}</em></button><div v-if="!meetings.length" class="empty-state">Создайте первое совещание или загрузите запись.</div></div><div class="create-inline"><input v-model="title" placeholder="Название нового совещания" @keyup.enter="createMeeting" /><button class="primary" @click="createMeeting">＋ Создать</button></div></section><section class="panel quick-panel"><p class="eyebrow">БЫСТРЫЙ СТАРТ</p><h2>Запишите разговор</h2><p>Управляйте Recorder Agent из браузера. Запись продолжится даже при временной потере связи.</p><button class="primary wide" @click="selected ? recordingCommand('START') : activeNav = 'meetings'">● Начать запись</button><button class="secondary wide" @click="activeNav = 'agents'">Настроить источник</button></section></div><section v-if="selected" class="panel meeting-workspace"><div class="workspace-header"><div><p class="eyebrow">SELECTED MEETING</p><h1>{{ selected.title }}</h1><p class="muted">{{ selected.status }} · {{ transcriptStatus }}</p></div><div class="recording-actions"><button class="primary" @click="recordingCommand('START')">● Запись</button><button class="secondary" @click="recordingCommand('PAUSE')">Ⅱ Пауза</button><button class="danger" @click="recordingCommand('STOP')">■ Стоп</button></div></div><div class="player-card"><audio id="meeting-audio" :src="previewUrl" controls></audio><div class="waveform"><i v-for="n in 52" :key="n" :style="{ height: (18 + ((n * 17) % 42)) + 'px' }"></i></div><small v-if="!previewUrl">Preview появится после нормализации медиа.</small></div><div class="tabbar"><button v-for="tab in [{id:'overview',label:'Обзор'},{id:'transcript',label:'Стенограмма'},{id:'speakers',label:'Спикеры'},{id:'summary',label:'Саммари'},{id:'decisions',label:'Решения'},{id:'tasks',label:'Поручения'},{id:'files',label:'Файлы'}]" :key="tab.id" :class="{ active: activeTab === tab.id }" @click="activeTab = tab.id">{{ tab.label }}</button></div><div v-if="activeTab === 'overview'" class="overview-grid"><div><h3>Состояние обработки</h3><div v-for="job in jobs" :key="job.id" class="job-row"><span>{{ job.type || 'JOB' }} · {{ job.stage }}</span><progress :value="job.progress" max="100"></progress><b>{{ job.status }}</b><button v-if="job.status === 'FAILED'" class="link-button" @click="retryJob(job)">Повторить</button></div></div><div class="upload-box"><h3>Добавить запись</h3><p>Аудио или видео → FFmpeg → WhisperX → Qwen.</p><input type="file" accept="audio/*,video/*,.flac,.wav,.m4a,.mp4,.mkv,.mov,.webm" @change="chooseFile" /><button class="primary wide" :disabled="!file || busy" @click="upload">{{ busy ? 'Загрузка…' : 'Загрузить и обработать' }}</button></div></div><div v-else-if="activeTab === 'transcript'" class="transcript-panel"><div class="transcript-toolbar"><span>{{ segments.length }} сегментов · {{ transcriptStatus }}</span><button class="secondary" @click="refreshSelected">Обновить</button></div><button v-for="segment in segments" :key="segment.id" class="segment-row" @click="seek(segment)"><span class="timecode">{{ timecode(segment.startMs) }}</span><span class="speaker-label">{{ segment.speaker || segment.speakerLabel || 'Спикер N' }}</span><span class="segment-text">{{ segment.text }}</span><small>{{ segment.confidence ? Math.round(segment.confidence * 100) + '%' : '' }}</small></button><div v-if="!segments.length" class="empty-state">Стенограмма появится после этапов ASR, alignment и diarization.</div></div><div v-else-if="activeTab === 'speakers'" class="speaker-list"><div v-for="speaker in speakers" :key="speaker.id" class="speaker-row"><div><strong>{{ speaker.displayName || 'Спикер N' }}</strong><small>{{ speaker.stableKey }} · {{ speaker.confidence ? Math.round(speaker.confidence * 100) + '%' : 'требует проверки' }}</small></div><button class="secondary" @click="renameSpeaker(speaker)">Переименовать</button></div><div v-if="!speakers.length" class="empty-state">Спикеры появятся после диаризации.</div></div><div v-else-if="activeTab === 'summary'" class="summary-panel"><div class="section-heading"><h3>Итог совещания</h3><button class="secondary" @click="rebuildSummary">Пересобрать</button></div><p v-if="summaryContent.summary" class="summary-text">{{ summaryContent.summary }}</p><div v-else class="empty-state">Саммари будет создано автоматически после стенограммы.</div><div class="tag-list"><span v-for="topic in (summaryContent.topics || [])" :key="topic">{{ topic }}</span></div><h3>Риски и открытые вопросы</h3><ul><li v-for="item in [...(summaryContent.risks || []), ...(summaryContent.open_questions || [])]" :key="item.text">{{ item.text }}</li></ul></div><div v-else-if="activeTab === 'decisions'" class="decision-list"><div v-for="decision in decisions" :key="decision.id" class="decision-row"><span class="decision-icon">✓</span><span>{{ decision.text }}</span><small>{{ decision.status }}</small></div><div v-if="!decisions.length" class="empty-state">Решения появятся в саммари.</div></div><div v-else-if="activeTab === 'tasks'" class="task-table"><div class="table-head"><span>Поручение</span><span>Ответственный</span><span>Срок</span><span>Статус</span></div><button v-for="task in tasks" :key="task.id" class="task-row" @click="updateTask(task)"><span>{{ task.task }}</span><span>{{ task.responsible || 'Не назначен' }}</span><span>{{ task.deadline ? new Date(task.deadline).toLocaleDateString() : 'Без срока' }}</span><span class="status-pill">{{ task.status }}</span></button><div v-if="!tasks.length" class="empty-state">Поручения появятся после автоматического саммари.</div></div><div v-else class="file-list"><div v-for="asset in media" :key="asset.id"><strong>{{ asset.originalName }}</strong><small>{{ asset.status }} · {{ Math.round(asset.sizeBytes / 1024 / 1024 * 10) / 10 }} MB</small></div></div></section></template>
      </section>
    </main>`
};

createApp(app).mount("#app");
