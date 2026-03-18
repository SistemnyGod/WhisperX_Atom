from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, UploadFile, File, HTTPException
from fastapi.responses import HTMLResponse
from fastapi.staticfiles import StaticFiles
from fastapi.templating import Jinja2Templates
from starlette.requests import Request

from app.storage import (
    ensure_dirs,
    save_upload,
    job_path,
    read_job_json,
    write_job_json,
)
from app.transcription_pipeline import pipeline


@asynccontextmanager
async def lifespan(app: FastAPI):
    await pipeline.start()
    try:
        yield
    finally:
        await pipeline.stop()


app = FastAPI(title="WhisperX Web", lifespan=lifespan)

BASE_DIR = Path(__file__).resolve().parent
app.mount("/static", StaticFiles(directory=BASE_DIR / "static"), name="static")
templates = Jinja2Templates(directory=BASE_DIR / "templates")

ensure_dirs()


@app.get("/", response_class=HTMLResponse)
def index(request: Request):
    return templates.TemplateResponse("index.html", {"request": request})


@app.post("/api/jobs")
async def create_job(file: UploadFile = File(...)):
    if not file.filename:
        raise HTTPException(status_code=400, detail="No filename")

    job_id, audio_path = await save_upload(file)

    write_job_json(
        job_id,
        {
            "job_id": job_id,
            "status": "queued",
            "stage": "queued",
            "audio_path": str(audio_path),
            "error": None,
            "result": None,
        },
    )

    await pipeline.submit(job_id)
    return {"job_id": job_id}


@app.get("/api/jobs/{job_id}")
def get_job(job_id: str):
    p = job_path(job_id)
    if not p.exists():
        raise HTTPException(status_code=404, detail="Job not found")
    return read_job_json(job_id)


@app.get("/api/jobs/{job_id}/text")
def get_job_text(job_id: str):
    data = read_job_json(job_id)
    if data.get("status") != "done":
        raise HTTPException(status_code=409, detail="Job not done")
    return {"text": data["result"]["text"]}


@app.get("/health")
def health():
    return {"ok": True}
