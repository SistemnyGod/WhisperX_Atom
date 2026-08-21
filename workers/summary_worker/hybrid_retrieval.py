"""Deterministic, bounded hybrid retrieval for Assistant evidence.

The Assistant must remain useful when a question is phrased differently from
the transcript.  This module combines PostgreSQL Russian FTS with an
embedding signal and a small lexical signal, then applies a deterministic
rerank.  The default embedding provider is deliberately local and dependency
free: stable hashed token/character features.  Deployments may opt into a
real ``sentence-transformers`` provider through ``ASSISTANT_EMBEDDING_PROVIDER``
without changing the retrieval or evidence contracts.

This module never decides scope.  Callers must provide candidates that have
already passed meeting/RBAC/quality filters.  Neighbour expansion is likewise
limited to the same meeting and transcript supplied by the caller.
"""

from __future__ import annotations

from collections import OrderedDict
from dataclasses import dataclass
import hashlib
import math
import os
from pathlib import Path
import re
from typing import Any, Iterable, Protocol


_TOKEN_RE = re.compile(r"[\wА-Яа-яЁё-]{2,}", re.UNICODE)
_STOPWORDS = {
    "это", "этот", "эта", "эти", "что", "как", "кто", "где", "когда", "были", "было",
    "будет", "есть", "для", "при", "или", "и", "в", "во", "на", "по", "из", "с", "со",
    "у", "к", "о", "об", "за", "не", "нет", "да", "так", "мы", "они", "он", "она", "их",
    "его", "ее", "может", "можно", "нужно", "про", "под", "над", "до", "после",
}
_SUFFIXES = (
    "иями", "ями", "ами", "ого", "ему", "ому", "ее", "ие", "ые", "ов", "ев", "ам", "ям",
    "ах", "ях", "ы", "и", "а", "я", "у", "ю", "е", "о",
)

# A small, transparent vocabulary helps questions such as "кто отвечает"
# match a transcript saying "ответственный назначен".  It is intentionally
# conservative; it does not bypass the grounding validator.
_SYNONYM_GROUPS = (
    ("реш", "договор", "соглас", "утверд", "план"),
    ("ремонт", "почин", "исправ", "обслуж"),
    ("ответствен", "отвеч", "исполн", "назнач"),
    ("срок", "дата", "дедлайн", "когда"),
    ("стоим", "сумм", "бюджет", "цен"),
    ("встреч", "совещ", "обсужд"),
)


def normalize_token(token: str) -> str:
    value = token.lower().replace("ё", "е").strip("-")
    if not value or value in _STOPWORDS:
        return ""
    for suffix in _SUFFIXES:
        if len(value) - len(suffix) >= 4 and value.endswith(suffix):
            value = value[: -len(suffix)]
            break
    return value


def normalized_tokens(value: str) -> set[str]:
    return {token for raw in _TOKEN_RE.findall(value or "") if (token := normalize_token(raw))}


def _feature_tokens(value: str) -> set[str]:
    tokens = normalized_tokens(value)
    features = set(tokens)
    for token in tokens:
        # Character n-grams preserve useful Russian morphology and provide a
        # cheap semantic-ish signal for inflections and compound terms.
        padded = f"^{token}$"
        features.update(padded[index : index + 3] for index in range(max(0, len(padded) - 2)))
    for group in _SYNONYM_GROUPS:
        if any(any(term in token for term in group) for token in tokens):
            features.add("syn:" + group[0])
    return features


class EmbeddingProvider(Protocol):
    name: str
    dimension: int

    def embed(self, text: str) -> tuple[float, ...]:
        ...


class HashedEmbeddingProvider:
    """Stable local embedding with bounded CPU and memory usage.

    It is not a generative model and never sends transcript text anywhere.
    The feature hashing makes the baseline deterministic across workers and
    allows a real provider to be introduced later without changing ranking
    or evidence semantics.
    """

    name = "hashed-local-v1"

    def __init__(self, dimension: int = 384) -> None:
        self.dimension = max(64, min(int(dimension), 2048))

    def embed(self, text: str) -> tuple[float, ...]:
        values = [0.0] * self.dimension
        features = _feature_tokens(text)
        if not features:
            return tuple(values)
        for feature in features:
            digest = hashlib.blake2b(feature.encode("utf-8"), digest_size=16).digest()
            index = int.from_bytes(digest[:8], "big") % self.dimension
            sign = 1.0 if digest[8] & 1 else -1.0
            # Preserve high-signal normalized words and the explicit synonym
            # groups above the character n-gram noise.  This makes a phrase
            # such as "кто отвечает" rank near "ответственный назначен"
            # while unrelated text stays below the hybrid threshold.
            if feature.startswith("syn:"):
                base_weight = 4.0
            elif feature.startswith("^") or feature.endswith("$"):
                base_weight = 0.5
            else:
                base_weight = 2.0
            weight = base_weight + (digest[9] / 255.0) * (base_weight * 0.25)
            values[index] += sign * weight
        norm = math.sqrt(sum(value * value for value in values))
        return tuple(value / norm for value in values) if norm else tuple(values)

    def embed_many(self, texts: list[str]) -> list[tuple[float, ...]]:
        return [self.embed(text) for text in texts]


class SentenceTransformerEmbeddingProvider:
    """Optional real local embedding provider.

    Import/model loading is lazy so the default Summary Worker image remains
    lightweight.  If the optional package or model is absent, construction
    raises and the caller falls back to ``HashedEmbeddingProvider``.
    """

    def __init__(self, model_name: str) -> None:
        from sentence_transformers import SentenceTransformer  # type: ignore

        self._model = SentenceTransformer(model_name)
        self.name = f"sentence-transformers:{model_name}"
        self.dimension = int(self._model.get_sentence_embedding_dimension())

    def embed(self, text: str) -> tuple[float, ...]:
        values = self._model.encode(text or "", normalize_embeddings=True, convert_to_numpy=True)
        return tuple(float(value) for value in values)

    def embed_many(self, texts: list[str]) -> list[tuple[float, ...]]:
        values = self._model.encode(texts, normalize_embeddings=True, convert_to_numpy=True, batch_size=32, show_progress_bar=False)
        return [tuple(float(value) for value in row) for row in values]


def create_embedding_provider() -> EmbeddingProvider:
    requested = os.getenv("ASSISTANT_EMBEDDING_PROVIDER", "auto").strip().lower()
    model = os.getenv("ASSISTANT_EMBEDDING_MODEL", "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2").strip()
    # ``auto`` is offline-safe: it only probes a model already present in the
    # image/cache. An explicit sentence-transformers provider is the operator
    # opt-in for a model download prepared outside the worker startup path.
    model_is_local = Path(model).exists() or os.getenv("HF_HUB_OFFLINE", "").strip() == "1"
    if requested in {"sentence-transformers", "sentence_transformers", "local-model"} or (requested == "auto" and model_is_local):
        try:
            return SentenceTransformerEmbeddingProvider(model)
        except Exception:
            # Retrieval must not make Assistant unavailable.  The fallback is
            # explicit in diagnostics and still works fully offline.
            pass
    return HashedEmbeddingProvider(int(os.getenv("ASSISTANT_EMBEDDING_DIMENSION", "384")))


@dataclass(frozen=True)
class RetrievalCandidate:
    segment_id: str
    meeting_id: str
    transcript_id: str
    transcript_version: int
    start_ms: int
    end_ms: int
    speaker: str
    text: str
    version_kind: str
    ordinal: int
    fts_rank: float = 0.0
    meeting_created_at: Any = None


@dataclass(frozen=True)
class RankedCandidate:
    candidate: RetrievalCandidate
    fts_score: float
    embedding_score: float
    lexical_score: float
    score: float


def _cosine(left: tuple[float, ...], right: tuple[float, ...]) -> float:
    if not left or not right:
        return 0.0
    size = min(len(left), len(right))
    value = sum(left[index] * right[index] for index in range(size))
    # Provider vectors are normally normalized. Clamp to tolerate optional
    # provider rounding and keep ranking stable.
    return max(-1.0, min(1.0, value))


class HybridRetriever:
    """Combine FTS, embeddings, neighbours and deterministic reranking."""

    def __init__(self, provider: EmbeddingProvider | None = None) -> None:
        self.provider = provider or create_embedding_provider()
        self._cache: OrderedDict[str, tuple[float, ...]] = OrderedDict()
        self._cache_limit = max(256, min(int(os.getenv("ASSISTANT_EMBEDDING_CACHE", "4096")), 20000))

    def _embed(self, text: str) -> tuple[float, ...]:
        key = hashlib.sha256((text or "").encode("utf-8")).hexdigest()
        cached = self._cache.get(key)
        if cached is not None:
            self._cache.move_to_end(key)
            return cached
        vector = self.provider.embed(text or "")
        self._cache[key] = vector
        self._cache.move_to_end(key)
        while len(self._cache) > self._cache_limit:
            self._cache.popitem(last=False)
        return vector

    def _embed_many(self, texts: list[str]) -> list[tuple[float, ...]]:
        result: list[tuple[float, ...] | None] = [None] * len(texts)
        missing: list[tuple[int, str, str]] = []
        for index, text in enumerate(texts):
            key = hashlib.sha256((text or "").encode("utf-8")).hexdigest()
            cached = self._cache.get(key)
            if cached is None:
                missing.append((index, key, text or ""))
            else:
                self._cache.move_to_end(key)
                result[index] = cached
        batch_size = max(16, min(int(os.getenv("ASSISTANT_EMBEDDING_BATCH_SIZE", "128")), 512))
        for offset in range(0, len(missing), batch_size):
            batch = missing[offset : offset + batch_size]
            provider_batch = getattr(self.provider, "embed_many", None)
            vectors = provider_batch([item[2] for item in batch]) if provider_batch else [self.provider.embed(item[2]) for item in batch]
            for (index, key, _), vector in zip(batch, vectors):
                self._cache[key] = vector
                self._cache.move_to_end(key)
                result[index] = vector
        while len(self._cache) > self._cache_limit:
            self._cache.popitem(last=False)
        return [vector or tuple() for vector in result]

    def rank(self, query: str, candidates: Iterable[RetrievalCandidate], limit: int = 12) -> list[RankedCandidate]:
        values = list(candidates)
        if not values or not (query or "").strip():
            return []
        query_tokens = normalized_tokens(query)
        query_vector = self._embed(query)
        candidate_vectors = self._embed_many([item.text for item in values])
        max_fts = max((float(item.fts_rank) for item in values), default=0.0)
        ranked: list[RankedCandidate] = []
        for index, item in enumerate(values):
            candidate_tokens = normalized_tokens(item.text)
            lexical = len(query_tokens & candidate_tokens) / max(1, len(query_tokens))
            embedding = (_cosine(query_vector, candidate_vectors[index]) + 1.0) / 2.0
            fts = min(1.0, max(0.0, float(item.fts_rank) / max_fts)) if max_fts > 0 else 0.0
            # FTS remains the strongest exact-match signal; embeddings rescue
            # paraphrases; lexical overlap makes the fallback explainable.
            score = 0.50 * fts + 0.38 * embedding + 0.12 * lexical
            ranked.append(RankedCandidate(item, fts, embedding, lexical, score))
        ranked.sort(key=lambda item: (-item.score, -item.fts_score, item.candidate.meeting_id, item.candidate.ordinal, item.candidate.segment_id))
        minimum = float(os.getenv("ASSISTANT_HYBRID_MIN_SCORE", "0.30"))
        embedding_minimum = float(os.getenv("ASSISTANT_HYBRID_EMBEDDING_MIN", "0.72"))
        # The hashed provider is a deterministic fallback, not a semantic
        # model. It must never create evidence from cosine similarity alone:
        # unrelated segments can collide in the feature hash. A real local
        # embedding provider may use the explicit semantic threshold.
        semantic_provider = self.provider.name.startswith("sentence-transformers:")
        selected = [
            item for item in ranked
            if item.score >= minimum
            and (item.fts_score > 0.0 or item.lexical_score > 0.0 or (semantic_provider and item.embedding_score >= embedding_minimum))
        ][: max(1, min(int(limit), 12))]
        return selected

    @staticmethod
    def expand_neighbours(anchors: Iterable[RankedCandidate], candidates: Iterable[RetrievalCandidate], limit: int = 36) -> list[RetrievalCandidate]:
        values = list(candidates)
        by_position = {(item.meeting_id, item.transcript_id, item.ordinal): item for item in values}
        selected: dict[str, RetrievalCandidate] = {}
        for ranked in anchors:
            item = ranked.candidate
            for ordinal in (item.ordinal - 1, item.ordinal, item.ordinal + 1):
                neighbour = by_position.get((item.meeting_id, item.transcript_id, ordinal))
                if neighbour is not None:
                    selected[neighbour.segment_id] = neighbour
        ordered = list(selected.values())
        ordered.sort(key=lambda item: (item.meeting_id, item.ordinal, item.segment_id))
        return ordered[: max(1, min(int(limit), 36))]
