"""The admin area's interpreter (ADR-125).

It reads aggregates and writes prose. What has to hold is that it stays
behind the token, uses its own analyst instruction rather than the tutor's,
and cleans whatever the model returns into the shape the backend reads.
"""

from __future__ import annotations

from app import insight_prompts


def _body(**overrides) -> dict:
    body = {
        "section": "Listening",
        "question": "لماذا انخفضت نسبة إكمال Listening هذا الشهر؟",
        "evidence": '{"evidence":[{"label":"Listening — الانسحاب","value":0.4}],"charts":[{"id":"failures_listening"}]}',
    }
    body.update(overrides)
    return body


def test_requires_the_service_token(client):
    assert client.post("/ai/admin/investigate", json=_body()).status_code == 401


def test_shapes_the_answer_and_uses_the_analyst_instruction(client, auth, stub_gemini):
    recorder = stub_gemini({
        "summary": "  الانسحاب من Listening مرتفع.  ",
        "interpretation": ["الانسحاب 40%", " ", "x"],
        "hypotheses": ["قد يكون الصوت سريعًا"],
        "investigate": [{"title": "افحص سرعة الصوت", "why": "الإعادة مرتفعة"}, {"title": "", "why": "drop"}, "junk"],
        "charts": ["failures_listening"],
    })

    response = client.post("/ai/admin/investigate", json=_body(), headers=auth)

    assert response.status_code == 200
    data = response.json()
    assert data["summary"] == "الانسحاب من Listening مرتفع."
    assert data["interpretation"] == ["الانسحاب 40%", "x"]
    assert [lead["title"] for lead in data["investigate"]] == ["افحص سرعة الصوت"]
    assert data["charts"] == ["failures_listening"]
    assert data["prompt_version"] == insight_prompts.INSIGHT_PROMPT_VERSION
    assert recorder.kwargs[0]["system_instruction"] == insight_prompts.SYSTEM
    # The evidence and the question reach the model; nothing else is added.
    assert "لماذا انخفضت" in recorder.prompts[0]
    assert "failures_listening" in recorder.prompts[0]


def test_refuses_an_oversized_evidence_document(client, auth):
    response = client.post(
        "/ai/admin/investigate", json=_body(evidence="x" * 60_001), headers=auth)
    assert response.status_code == 422
