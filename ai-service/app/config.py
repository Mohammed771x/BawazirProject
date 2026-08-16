"""Configuration for the WordOS AI service.

Every secret is read from the environment. Nothing here has a default that
would let the service start with a placeholder credential — a missing key is a
startup failure, because a service that silently runs without one produces
confusing 500s at request time instead of an obvious error at boot.
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


def _load_dotenv() -> None:
    """Load ``.env`` for local development.

    Deliberately hand-rolled rather than a dependency: it is fifteen lines, and
    the file is only read in development. Real deployments set environment
    variables directly, so this is a no-op there.
    """
    env_path = Path(__file__).resolve().parent.parent / ".env"
    if not env_path.exists():
        return

    for raw in env_path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        # Real environment variables win over the file, so a deployment can
        # override without editing anything.
        if key and key not in os.environ:
            os.environ[key] = value


_load_dotenv()


@dataclass(frozen=True)
class Settings:
    gemini_api_key: str
    gemini_model: str
    service_token: str

    @property
    def masked_key(self) -> str:
        """A fingerprint for logs. The key itself is never printed anywhere."""
        key = self.gemini_api_key
        if len(key) < 12:
            return "set (too short to fingerprint)"
        return f"{key[:4]}…{key[-4:]} ({len(key)} chars)"


class ConfigurationError(RuntimeError):
    pass


# Model tiers that cost materially more per token. Selecting one is a spending
# decision, so it must be deliberate: the product owner asked for the cheap
# flash tier only, and a stray edit or a copied example should not quietly
# start billing at pro rates.
_EXPENSIVE_MARKERS = ("-pro", "pro-", "ultra", "deep-research", "-max")


def _guard_model_cost(model: str) -> None:
    allow = os.environ.get("GEMINI_ALLOW_EXPENSIVE", "").strip().lower()
    if allow in ("1", "true", "yes"):
        return

    lowered = model.lower()
    if any(marker in lowered for marker in _EXPENSIVE_MARKERS):
        raise ConfigurationError(
            f"Refusing to use '{model}': it is a higher-cost tier.\n\n"
            "The project runs on the flash tier by decision. If a pro model is\n"
            "genuinely wanted for one task, opt in explicitly:\n\n"
            "  GEMINI_ALLOW_EXPENSIVE=true\n\n"
            "Leaving this guard on is what stops an accidental edit from\n"
            "spending the budget at several times the rate."
        )


def load_settings() -> Settings:
    key = os.environ.get("GEMINI_API_KEY", "").strip()
    if not key:
        raise ConfigurationError(
            "GEMINI_API_KEY is not set.\n\n"
            "  cd ai-service\n"
            "  cp .env.example .env\n"
            "  # then edit .env and paste your key\n\n"
            "The key must never appear in source, in Flutter, or in Git."
        )

    model = os.environ.get("GEMINI_MODEL", "gemini-3.1-flash-lite").strip()
    _guard_model_cost(model)

    return Settings(
        gemini_api_key=key,
        gemini_model=model,
        # Optional in development; required before anything is deployed, so the
        # AI service is not an open relay to a paid API for anyone who can
        # reach the port.
        service_token=os.environ.get("AI_SERVICE_TOKEN", "").strip(),
    )
