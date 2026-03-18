let mediaRecorder;
let chunks = [];
let blob = null;
let jobId = null;

const btnStart = document.getElementById('btnStart');
const btnStop = document.getElementById('btnStop');
const btnUpload = document.getElementById('btnUpload');
const player = document.getElementById('player');
const statusEl = document.getElementById('status');
const outputEl = document.getElementById('output');

function setStatus(obj) {
statusEl.textContent = typeof obj === 'string' ? obj : JSON.stringify(obj, null, 2);
}

function isSecureForMic() {
return window.isSecureContext || location.hostname === 'localhost' || location.hostname === '127.0.0.1';
}

function stopStreamTracks(stream) {
if (!stream) return;
for (const track of stream.getTracks()) {
track.stop();
}
}

function prettyMicError(err) {
if (!err) return 'Неизвестная ошибка доступа к микрофону';
const name = err.name || '';
if (name === 'NotAllowedError') return 'Доступ к микрофону запрещён. Разрешите доступ в браузере.';
if (name === 'NotFoundError') return 'Микрофон не найден. Проверьте устройство и настройки.';
if (name === 'NotReadableError') return 'Микрофон занят другим приложением.';
if (name === 'SecurityError') return 'Нужен защищённый контекст (HTTPS) или localhost.';
return `Ошибка микрофона: ${name || err.message || String(err)}`;
}

btnStart.onclick = async () => {
chunks = [];
blob = null;
jobId = null;
outputEl.value = '';
player.removeAttribute('src');
player.load();

if (!isSecureForMic()) {
setStatus('Запись доступна только по HTTPS или на localhost.');
return;
}

if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
setStatus('getUserMedia не поддерживается в этом браузере.');
return;
}

if (!window.MediaRecorder) {
setStatus('MediaRecorder не поддерживается в этом браузере.');
return;
}

btnStart.disabled = true;
btnStop.disabled = true;
btnUpload.disabled = true;

let stream;
try {
stream = await navigator.mediaDevices.getUserMedia({ audio: true });
} catch (err) {
btnStart.disabled = false;
btnStop.disabled = true;
setStatus(prettyMicError(err));
return;
}

try {
mediaRecorder = new MediaRecorder(stream);
} catch (err) {
stopStreamTracks(stream);
btnStart.disabled = false;
btnStop.disabled = true;
setStatus(prettyMicError(err));
return;
}

mediaRecorder.ondataavailable = (e) => {
if (e.data.size > 0) chunks.push(e.data);
};

mediaRecorder.onerror = (e) => {
setStatus(prettyMicError(e.error || e));
};

mediaRecorder.onstop = () => {
blob = new Blob(chunks, { type: 'audio/webm' });
player.src = URL.createObjectURL(blob);
btnUpload.disabled = false;
setStatus('Готово к загрузке');
stopStreamTracks(stream);
};

mediaRecorder.start();
btnStop.disabled = false;
setStatus('Запись...');
};

btnStop.onclick = () => {
if (!mediaRecorder) return;
if (mediaRecorder.state === 'inactive') return;
mediaRecorder.stop();
btnStart.disabled = false;
btnStop.disabled = true;
};

btnUpload.onclick = async () => {
if (!blob) return;
btnUpload.disabled = true;
setStatus('Загрузка...');

const fd = new FormData();
fd.append('file', blob, 'recording.webm');

const r = await fetch('/api/jobs', { method: 'POST', body: fd });
const data = await r.json();
jobId = data.job_id;

setStatus({ job_id: jobId, status: 'queued' });
pollJob(jobId);
};

async function pollJob(id) {
while (true) {
const r = await fetch(`/api/jobs/${id}`);
const data = await r.json();
setStatus(data);

if (data.status === 'done') {
outputEl.value = data.result.text || '';
return;
}

if (data.status === 'error') {
outputEl.value = data.error || 'Ошибка';
return;
}

await new Promise(res => setTimeout(res, 1500));
}
}