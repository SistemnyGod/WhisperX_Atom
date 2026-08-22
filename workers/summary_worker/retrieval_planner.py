"""Intent-aware retrieval plan kept separate from database retrieval."""

from __future__ import annotations

from dataclasses import dataclass

from .query_understanding import AssistantQueryPlan


@dataclass(frozen=True)
class RetrievalPlan:
    query: str
    intent: str
    anchor_limit: int
    neighbour_window: int
    split_topics: tuple[str, ...] = ()

    def to_dict(self) -> dict[str, object]:
        return {
            "query": self.query,
            "intent": self.intent,
            "anchorLimit": self.anchor_limit,
            "neighbourWindow": self.neighbour_window,
            "splitTopics": list(self.split_topics),
        }


def build_retrieval_plan(plan: AssistantQueryPlan, original_query: str) -> RetrievalPlan:
    # Comparison is kept as an explicit plan even before the two-bundle
    # execution path is enabled. This prevents the generic top-k path from
    # silently claiming that both alternatives were equally covered.
    split_topics: tuple[str, ...] = ()
    if plan.requires_comparison and " и " in original_query.lower():
        split_topics = tuple(item.strip() for item in original_query.lower().split(" и ", 1) if item.strip())
    return RetrievalPlan(
        query=plan.retrieval_query(original_query),
        intent=plan.intent,
        anchor_limit=64 if plan.intent in {"CAUSE", "COMPARISON", "SUMMARY"} else 48,
        neighbour_window=plan.neighbour_window,
        split_topics=split_topics,
    )

