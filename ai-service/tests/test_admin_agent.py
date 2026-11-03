"""WordOS AI's single step (ADR-133): behind the token, cleaned on the way out,
and unable to keep calling tools once the backend says to answer."""

from __future__ import annotations

from app import agent_prompts


def _body(**overrides) -> dict:
    body = {
        "message": "كم عدد المستخدمين النشطين خلال آخر 7 أيام؟",
        "conversation": [],
        "steps": [],
        "tools": "- metrics(keys, days): dictionary metrics",
        "dictionary": "active_learners: المستخدمون النشطون",
        "today": "2026-11-03",
    }
    body.update(overrides)
    return body


def test_requires_the_service_token(client):
    assert client.post("/ai/admin/agent", json=_body()).status_code == 401


def test_asks_for_tools_and_cleans_the_calls(client, auth, stub_gemini):
    recorder = stub_gemini({
        "action": "tools",
        "calls": [{"tool": " metrics ", "args_json": '{"keys":["active_learners"],"days":7}', "why": "count"},
                  {"tool": "", "args_json": "{}"}, "junk"],
        "answer": "",
    })
    data = client.post("/ai/admin/agent", json=_body(), headers=auth).json()
    assert data["action"] == "tools"
    assert [c["tool"] for c in data["calls"]] == ["metrics"]
    assert data["prompt_version"] == agent_prompts.AGENT_PROMPT_VERSION
    assert recorder.kwargs[0]["system_instruction"] == agent_prompts.SYSTEM
    assert "active_learners" in recorder.prompts[0]


def test_must_answer_turns_a_tool_request_into_an_answer(client, auth, stub_gemini):
    stub_gemini({"action": "tools", "calls": [{"tool": "metrics", "args_json": "{}"}], "answer": ""})
    data = client.post("/ai/admin/agent", json=_body(must_answer=True), headers=auth).json()
    assert data["action"] == "answer"
    assert data["calls"] == []
    assert data["answer"]


def test_the_conversation_and_earlier_data_reach_the_model(client, auth, stub_gemini):
    recorder = stub_gemini({"action": "answer", "answer": "B1 أبطأ.", "attachments": ["t1"]})
    body = _body(message="طيب وB1؟", conversation=[
        {"role": "user", "text": "ما وضع Reading؟"},
        {"role": "assistant", "text": "Reading مستقر.", "data": '{"reading.pass_rate":0.8}'},
    ])
    data = client.post("/ai/admin/agent", json=body, headers=auth).json()
    assert data["answer"] == "B1 أبطأ."
    assert "ما وضع Reading؟" in recorder.prompts[0]
    assert "reading.pass_rate" in recorder.prompts[0]
    assert data["title"] == ""  # only the first message names the conversation


def test_an_empty_answer_before_the_last_round_stays_empty(client, auth, stub_gemini):
    stub_gemini({"action": "answer", "answer": ""})
    data = client.post("/ai/admin/agent", json=_body(), headers=auth).json()
    assert data["action"] == "answer"
    assert data["answer"] == ""  # the backend asks again with must_answer
