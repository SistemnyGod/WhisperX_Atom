import { createApp, ref, computed, onMounted, onBeforeUnmount } from "vue";
import Uppy from "@uppy/core";
import Tus from "@uppy/tus";
import "./style.css";

type Meeting = { id: string; title: string; description?: string; status: string; createdAt: string };
type Job = { id: string; status: string; stage: string; progress: number; error?: string };
type Segment = { id: string; ordinal: number; startMs: number; endMs: number; speaker?: string; text: string; confidence?: number };
type Media = { id: string; originalName: string; sizeBytes: number; durationMs?: number; sha256?: string; status: string; previewStorageKey?: string };
type Speaker = { id: string; stableKey: string; displayName: string };

const app = {
  setup() {
    const username = ref("admin");
    const password = ref("change-me-now");
    const authenticated = ref(false);
    const meetings = ref<Meeting[]>([]);
    const selected = ref<Meeting | null>(null);
    const jobs = ref<Job[]>([]);
    const segments = ref<Segment[]>([]);
    const media = ref<Media[]>([]);
    const speakers = ref<Speaker[]>([]);
    const title = ref("");
    const description = ref("");
    const file = ref<File | null>(null);
    const error = ref("");
    const uppy = new Uppy({ autoProceed: false }).use(Tus, { endpoint: "/files/", chunkSize: 16 * 1024 * 1024, retryDelays: [0, 1000, 3000, 5000] });
    let eventSources: EventSource[] = [];

    async function api(path: string, options: RequestInit = {}) {
      const response = await fetch(path, { credentials: "include", ...options });
      if (!response.ok) throw new Error((await response.text()) || response.statusText);
      return response.status === 204 ? null : response.json();
    }

    async function login() {
      error.value = "";
      try { await api("/api/auth/login", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ username: username.value, password: password.value }) }); authenticated.value = true; await loadMeetings(); }
      catch { error.value = "Не удалось войти"; }
    }

    async function loadMeetings() { meetings.value = await api("/api/meetings"); }

    async function createMeeting() {
      if (!title.value.trim()) return;
      const meeting = await api("/api/meetings", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ title: title.value, description: description.value || null }) });
      title.value = ""; description.value = ""; await loadMeetings(); await selectMeeting(meeting);
    }

    function closeEvents() { eventSources.forEach((source) => source.close()); eventSources = []; }

    async function refreshSelected() {
      if (!selected.value) return;
      jobs.value = await api(`/api/meetings/${selected.value.id}/jobs`);
      const transcript = await api(`/api/meetings/${selected.value.id}/transcript`);
      segments.value = transcript.segments || [];
      media.value = await api(`/api/meetings/${selected.value.id}/media`);
      speakers.value = await api(`/api/meetings/${selected.value.id}/speakers`).catch(() => []);
      subscribeJobs();
    }

    function subscribeJobs() {
      closeEvents();
      for (const job of jobs.value) {
        const source = new EventSource(`/api/jobs/${job.id}/events`);
        source.addEventListener("progress", async (event) => {
          const update = JSON.parse((event as MessageEvent).data) as Job;
          const index = jobs.value.findIndex((item) => item.id === update.id);
          if (index >= 0) jobs.value[index] = update; else jobs.value.push(update);
          if (update.status === "READY" || update.status === "FAILED") await refreshSelected();
        });
        source.onerror = () => source.close();
        eventSources.push(source);
      }
    }

    async function selectMeeting(meeting: Meeting) { selected.value = meeting; await refreshSelected(); }

    function chooseFile(event: Event) { file.value = (event.target as HTMLInputElement).files?.[0] || null; }

    async function upload() {
      if (!selected.value || !file.value) return;
      error.value = "";
      try {
        const reservation = await api(`/api/meetings/${selected.value.id}/uploads`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ fileName: file.value.name, sizeBytes: file.value.size }) });
        uppy.cancelAll(); uppy.reset();
        const fileId = uppy.addFile({ name: file.value.name, type: file.value.type || "application/octet-stream", data: file.value });
        uppy.setFileMeta(fileId, { reservationId: reservation.uploadId, filename: file.value.name, filetype: file.value.type || "" });
        const result = await uppy.upload();
        if (!result?.successful?.length) throw new Error("Загрузка не завершена");
        error.value = "Файл загружен, ожидается автоматический hook и обработка.";
        for (let i = 0; i < 15 && !jobs.value.length; i++) { await new Promise((resolve) => setTimeout(resolve, 2000)); await refreshSelected(); }
      } catch (exc) { error.value = exc instanceof Error ? exc.message : "Ошибка загрузки"; }
    }

    async function renameSpeaker(speaker: Speaker) {
      const name = window.prompt("Имя спикера", speaker.displayName);
      if (name === null || !name.trim()) return;
      await api(`/api/meetings/${selected.value?.id}/speakers/${speaker.id}`, { method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ displayName: name.trim() }) });
      await refreshSelected();
    }

    async function mergeSpeaker(source: Speaker) {
      const targetName = window.prompt("Объединить с именем спикера", "");
      const target = speakers.value.find((item) => item.displayName === targetName && item.id !== source.id);
      if (!target || !selected.value) return;
      await api(`/api/meetings/${selected.value.id}/speakers/merge`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ sourceSpeakerId: source.id, targetSpeakerId: target.id }) });
      await refreshSelected();
    }

    function seek(segment: Segment) { const audio = document.querySelector<HTMLAudioElement>("#meeting-audio"); if (audio) { audio.currentTime = segment.startMs / 1000; audio.play().catch(() => undefined); } }
    const previewUrl = computed(() => media.value[0] ? `/api/media/${media.value[0].id}/preview` : "");
    const selectedTitle = computed(() => selected.value?.title || "Выберите совещание");

    onMounted(async () => { try { await api("/api/auth/me"); authenticated.value = true; await loadMeetings(); } catch { /* login */ } });
    onBeforeUnmount(closeEvents);

    return { username, password, authenticated, meetings, selected, jobs, segments, media, speakers, title, description, file, error, selectedTitle, previewUrl, login, createMeeting, selectMeeting, chooseFile, upload, seek, renameSpeaker, mergeSpeaker };
  },
  template: `
    <main class="shell">
      <section v-if="!authenticated" class="card login"><h1>WhisperX Atom</h1><p>Локальная история стенограмм</p><input v-model="username" placeholder="Пользователь" /><input v-model="password" type="password" placeholder="Пароль" /><button @click="login">Войти</button><small v-if="error" class="error">{{ error }}</small></section>
      <template v-else>
        <header class="topbar"><div><strong>WhisperX Atom</strong><span>server-first MVP</span></div><button class="secondary" @click="loadMeetings">Обновить</button></header>
        <div class="layout">
          <aside class="card sidebar"><h2>Совещания</h2><div class="create"><input v-model="title" placeholder="Название" /><input v-model="description" placeholder="Описание (необязательно)" /><button @click="createMeeting">Создать</button></div><button v-for="meeting in meetings" :key="meeting.id" class="meeting" :class="{ active: selected?.id === meeting.id }" @click="selectMeeting(meeting)"><strong>{{ meeting.title }}</strong><small>{{ new Date(meeting.createdAt).toLocaleString() }}</small></button></aside>
          <section class="card content"><h1>{{ selectedTitle }}</h1><div v-if="!selected" class="empty">Создайте или выберите совещание.</div><template v-else>
            <div class="toolbar"><input type="file" accept=".wav,.flac,.mp3,.m4a,.aac,.ogg,.opus" @change="chooseFile" /><button @click="upload" :disabled="!file">Загрузить и обработать</button></div><p v-if="error" class="error">{{ error }}</p>
            <audio id="meeting-audio" :src="previewUrl" controls></audio>
            <h2>Медиа</h2><div v-for="asset in media" :key="asset.id" class="job"><span>{{ asset.originalName }}</span><small>{{ asset.status }} · {{ asset.sizeBytes }} bytes · {{ asset.sha256 || "SHA ожидается" }}</small></div>
            <h2>Обработка</h2><div v-for="job in jobs" :key="job.id" class="job"><span>{{ job.stage }}</span><progress :value="job.progress" max="100"></progress><small>{{ job.status }}<span v-if="job.error"> — {{ job.error }}</span></small></div>
            <h2>Спикеры</h2><div v-if="!speakers.length" class="empty">Спикеры появятся после диаризации.</div><div v-for="speaker in speakers" :key="speaker.id" class="speaker"><span>{{ speaker.displayName }}</span><button class="secondary" @click="renameSpeaker(speaker)">Переименовать</button><button class="secondary" @click="mergeSpeaker(speaker)">Объединить</button></div>
            <h2>Стенограмма</h2><div v-if="!segments.length" class="empty">Сегменты появятся после обработки.</div><button v-for="segment in segments" :key="segment.id" class="segment" @click="seek(segment)"><span class="time">{{ Math.floor(segment.startMs / 60000).toString().padStart(2, "0") }}:{{ Math.floor(segment.startMs / 1000 % 60).toString().padStart(2, "0") }}</span><b>{{ segment.speaker || "Спикер N" }}</b><span>{{ segment.text }}</span></button>
          </template></section>
        </div>
      </template>
    </main>`
};

createApp(app).mount("#app");
