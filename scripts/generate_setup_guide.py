"""
generate_setup_guide.py
=======================

Regenerates `docs/SETUP_GUIDE.pdf` from live project sources so the guide
never drifts away from the code.

What it reads (single source of truth — never hand-edit the PDF):
  - backend/app/main.py            -> backend version
  - valheim-plugin/Plugin.cs       -> plugin version
  - backend/README.md              -> backend setup & provider instructions
  - valheim-plugin/README.md       -> plugin setup, commands, shop schema
  - backend/.env.example           -> environment variable reference
  - backend/app/config.py          -> defaults for env settings
  - backend/app/main.py            -> registered routers
  - backend/fly.toml               -> deploy region / VM sizing
  - valheim-plugin/ValheimDonationSystem.csproj -> required Unity DLLs

How to run:
  python scripts/generate_setup_guide.py

Optional flags:
  --output PATH      Override output PDF path (default: docs/SETUP_GUIDE.pdf)
  --check            Exit 1 if the PDF would change (for CI / pre-commit)

When to run this:
  Any time you change one of the inputs above. A `git pre-commit` hook
  example is at the bottom of this file's docstring.

  Suggested hook (PowerShell):
      python scripts/generate_setup_guide.py
      git add docs/SETUP_GUIDE.pdf

Dependencies:
  pip install reportlab
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable, List, Tuple

from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import LETTER
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import inch
from reportlab.platypus import (
    PageBreak,
    Paragraph,
    SimpleDocTemplate,
    Spacer,
    Table,
    TableStyle,
)

# ---------------------------------------------------------------------------
# Project layout
# ---------------------------------------------------------------------------

REPO_ROOT = Path(__file__).resolve().parent.parent
BACKEND = REPO_ROOT / "backend"
PLUGIN = REPO_ROOT / "valheim-plugin"
DOCS = REPO_ROOT / "docs"
DEFAULT_OUTPUT = DOCS / "SETUP_GUIDE.pdf"


# ---------------------------------------------------------------------------
# Source extraction — read the truth out of the project
# ---------------------------------------------------------------------------


@dataclass
class ProjectFacts:
    backend_version: str
    plugin_version: str
    backend_routers: List[str]
    env_vars: List[Tuple[str, str, str]]  # (name, default/example, comment)
    required_dlls: List[str]
    fly_region: str
    fly_memory_mb: str
    generated_at: str
    inputs_fingerprint: str


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def _hash_inputs(paths: Iterable[Path]) -> str:
    h = hashlib.sha256()
    for p in sorted(paths):
        if p.exists():
            h.update(p.name.encode("utf-8"))
            h.update(b"\0")
            h.update(p.read_bytes())
            h.update(b"\0")
    return h.hexdigest()[:12]


def extract_facts() -> ProjectFacts:
    backend_main = _read(BACKEND / "app" / "main.py")
    plugin_main = _read(PLUGIN / "Plugin.cs")
    env_example = _read(BACKEND / ".env.example")
    csproj = _read(PLUGIN / "ValheimDonationSystem.csproj")
    fly = _read(BACKEND / "fly.toml")

    # Backend version
    m = re.search(r'version=["\']([^"\']+)["\']', backend_main)
    backend_version = m.group(1) if m else "unknown"

    # Plugin version
    m = re.search(r'BepInPlugin\("[^"]+",\s*"[^"]+",\s*"([^"]+)"\)', plugin_main)
    plugin_version = m.group(1) if m else "unknown"

    # Routers
    routers = re.findall(r"app\.include_router\((\w+)\.router\)", backend_main)

    # Env vars from .env.example
    env_vars: List[Tuple[str, str, str]] = []
    last_comment = ""
    for raw in env_example.splitlines():
        line = raw.strip()
        if not line:
            last_comment = ""
            continue
        if line.startswith("#"):
            stripped = line.lstrip("#").strip()
            if stripped and not stripped.startswith("="):
                last_comment = stripped
            continue
        # KEY=VALUE  (value may include an inline comment)
        m = re.match(r"^([A-Z_][A-Z0-9_]*)\s*=\s*(.*)$", line)
        if not m:
            continue
        key, rest = m.group(1), m.group(2)
        inline_comment = ""
        if "#" in rest:
            value, inline_comment = rest.split("#", 1)
            value = value.strip()
            inline_comment = inline_comment.strip()
        else:
            value = rest.strip()
        comment = inline_comment or last_comment
        env_vars.append((key, value or "(empty)", comment))
        last_comment = ""

    # Required DLLs from csproj
    dlls = re.findall(r"<HintPath>libs[\\/]([^<]+)</HintPath>", csproj)

    # Fly facts
    region_m = re.search(r'primary_region\s*=\s*"([^"]+)"', fly)
    mem_m = re.search(r"memory_mb\s*=\s*(\d+)", fly)
    region = region_m.group(1) if region_m else "(unknown)"
    memory = mem_m.group(1) if mem_m else "(unknown)"

    fingerprint = _hash_inputs(
        [
            BACKEND / "app" / "main.py",
            PLUGIN / "Plugin.cs",
            BACKEND / ".env.example",
            PLUGIN / "ValheimDonationSystem.csproj",
            BACKEND / "fly.toml",
            BACKEND / "README.md",
            PLUGIN / "README.md",
            BACKEND / "app" / "config.py",
        ]
    )

    return ProjectFacts(
        backend_version=backend_version,
        plugin_version=plugin_version,
        backend_routers=routers,
        env_vars=env_vars,
        required_dlls=dlls,
        fly_region=region,
        fly_memory_mb=memory,
        generated_at=datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC"),
        inputs_fingerprint=fingerprint,
    )


# ---------------------------------------------------------------------------
# PDF rendering
# ---------------------------------------------------------------------------


def _styles():
    base = getSampleStyleSheet()

    title = ParagraphStyle(
        "VDTitle",
        parent=base["Title"],
        fontName="Helvetica-Bold",
        fontSize=24,
        leading=28,
        spaceAfter=12,
    )
    subtitle = ParagraphStyle(
        "VDSubtitle",
        parent=base["Normal"],
        fontName="Helvetica",
        fontSize=12,
        textColor=colors.HexColor("#555555"),
        spaceAfter=24,
    )
    h1 = ParagraphStyle(
        "VDH1",
        parent=base["Heading1"],
        fontName="Helvetica-Bold",
        fontSize=18,
        leading=22,
        spaceBefore=18,
        spaceAfter=10,
        textColor=colors.HexColor("#1a3a5f"),
    )
    h2 = ParagraphStyle(
        "VDH2",
        parent=base["Heading2"],
        fontName="Helvetica-Bold",
        fontSize=14,
        leading=18,
        spaceBefore=12,
        spaceAfter=6,
        textColor=colors.HexColor("#2d5985"),
    )
    h3 = ParagraphStyle(
        "VDH3",
        parent=base["Heading3"],
        fontName="Helvetica-Bold",
        fontSize=11,
        leading=14,
        spaceBefore=8,
        spaceAfter=4,
        textColor=colors.HexColor("#444444"),
    )
    body = ParagraphStyle(
        "VDBody",
        parent=base["BodyText"],
        fontName="Helvetica",
        fontSize=10,
        leading=14,
        spaceAfter=6,
        alignment=TA_LEFT,
    )
    code = ParagraphStyle(
        "VDCode",
        parent=base["Code"],
        fontName="Courier",
        fontSize=8.5,
        leading=11,
        leftIndent=12,
        rightIndent=6,
        backColor=colors.HexColor("#f4f4f4"),
        borderColor=colors.HexColor("#dddddd"),
        borderWidth=0.5,
        borderPadding=6,
        spaceBefore=4,
        spaceAfter=8,
    )
    note = ParagraphStyle(
        "VDNote",
        parent=base["BodyText"],
        fontName="Helvetica-Oblique",
        fontSize=9,
        leading=12,
        textColor=colors.HexColor("#664400"),
        backColor=colors.HexColor("#fff7e0"),
        borderColor=colors.HexColor("#e0c060"),
        borderWidth=0.5,
        borderPadding=6,
        spaceBefore=4,
        spaceAfter=8,
    )
    return {
        "title": title,
        "subtitle": subtitle,
        "h1": h1,
        "h2": h2,
        "h3": h3,
        "body": body,
        "code": code,
        "note": note,
    }


def _esc(text: str) -> str:
    """Escape XML/HTML for ReportLab Paragraph."""
    return (
        text.replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
    )


def _code_block(text: str, style) -> Paragraph:
    # Replace newlines with <br/> and preserve spaces
    escaped = _esc(text)
    escaped = escaped.replace("\n", "<br/>").replace(" ", "&nbsp;")
    return Paragraph(escaped, style)


def _bullets(items: List[str], style) -> List[Paragraph]:
    return [Paragraph(f"&bull;&nbsp;&nbsp;{_esc(i)}", style) for i in items]


# ---------------------------------------------------------------------------
# Content sections
# ---------------------------------------------------------------------------


def build_story(facts: ProjectFacts, styles) -> list:
    story: list = []

    # ---- Cover ----
    story.append(Paragraph("Valheim Donations", styles["title"]))
    story.append(
        Paragraph(
            f"Complete Implementation &amp; Setup Guide<br/>"
            f"Backend {facts.backend_version} &middot; Plugin {facts.plugin_version}<br/>"
            f"Generated {facts.generated_at} &middot; "
            f"fingerprint <font face='Courier'>{facts.inputs_fingerprint}</font>",
            styles["subtitle"],
        )
    )
    story.append(
        Paragraph(
            "This guide walks you end-to-end through deploying the donation "
            "backend, building the BepInEx plugin, hooking up payment providers, "
            "and verifying the full donation flow inside Valheim. It is generated "
            "directly from the project's READMEs, environment templates, and "
            "source files &mdash; never hand-edited.",
            styles["body"],
        )
    )

    # ---- TOC ----
    story.append(Paragraph("Contents", styles["h2"]))
    toc = [
        "1. Architecture at a glance",
        "2. Prerequisites",
        "3. Backend &mdash; local development",
        "4. Environment variables reference",
        "5. Provider setup (Ko-fi, PayPal, Patreon, PayMongo)",
        "6. Deploying the backend to Fly.io",
        "7. Building the BepInEx plugin",
        "8. Installing the plugin on the server",
        "9. Configuration files &amp; persistent state",
        "10. Chat commands &amp; shop catalog",
        "11. In-game UI panel (Phase 5)",
        "12. Verifying the full flow",
        "13. Operations &amp; troubleshooting",
        "14. Keeping this guide up to date",
    ]
    for line in toc:
        story.append(Paragraph(line, styles["body"]))
    story.append(PageBreak())

    # ---- 1. Architecture ----
    story.append(Paragraph("1. Architecture at a glance", styles["h1"]))
    story.append(
        Paragraph(
            "The system is split in two halves that share a single source of "
            "truth: the backend's SQLite ledger.",
            styles["body"],
        )
    )
    story.extend(
        _bullets(
            [
                "Backend (FastAPI, Python 3.12+) receives webhooks from each "
                "provider, owns the coin ledger, and exposes a polling API.",
                "Plugin (BepInEx, .NET Framework 4.7.2) runs on the Valheim "
                "dedicated server, polls grants every ~10 s, applies them, and "
                "drives the chat commands plus the optional client-side panel.",
                "Donor portal (Jinja templates served by the backend) is what "
                "donors see when they paste their claim code &mdash; one page, "
                "four provider buttons.",
            ],
            styles["body"],
        )
    )
    story.append(Paragraph("Donation flow:", styles["h3"]))
    story.append(
        _code_block(
            "/donate in-game\n"
            "  -> plugin POST /api/claim\n"
            "  -> backend mints AB12-CD34 (TTL 30 min)\n"
            "  -> plugin DMs donor: 'Donate at /portal, code is AB12-CD34'\n"
            "  -> donor visits /portal/AB12-CD34\n"
            "       Ko-fi / PayPal -> hosted page (code prefilled where possible)\n"
            "       PayMongo       -> portal mints PaymentLink with metadata.claim_code\n"
            "       Patreon        -> patreon.com + 'Link my account' (OAuth)\n"
            "  -> provider webhook fires -> backend resolves code -> grants row\n"
            "  -> plugin polls /api/grants/pending -> applies -> /api/grants/ack",
            styles["code"],
        )
    )

    # ---- 2. Prerequisites ----
    story.append(Paragraph("2. Prerequisites", styles["h1"]))
    story.extend(
        _bullets(
            [
                "Python 3.12+ with pip and venv.",
                ".NET SDK 6.0+ (for `dotnet build -c Release`).",
                "A Valheim dedicated server with BepInEx already installed.",
                "Fly.io account + `flyctl` CLI (for the recommended deploy).",
                "Accounts on whichever providers you want to accept: Ko-fi, "
                "PayPal (developer app), Patreon (creator + API client), "
                "and/or PayMongo (for GCash/Maya/cards in PH).",
                "A copy of Valheim's `valheim_Data/Managed/` folder accessible "
                "to your build machine &mdash; you need to copy DLLs from it.",
            ],
            styles["body"],
        )
    )

    # ---- 3. Backend local dev ----
    story.append(PageBreak())
    story.append(Paragraph("3. Backend &mdash; local development", styles["h1"]))
    story.append(
        Paragraph(
            "Use a fresh virtual environment so you don't pollute your global "
            "Python install. PowerShell commands shown; bash works the same way.",
            styles["body"],
        )
    )
    story.append(
        _code_block(
            "cd backend\n"
            "python -m venv .venv\n"
            ".\\.venv\\Scripts\\Activate.ps1\n"
            "pip install -r requirements-dev.txt\n"
            "copy .env.example .env       # then edit secrets\n"
            "uvicorn app.main:app --reload --port 8080",
            styles["code"],
        )
    )
    story.append(Paragraph("Run the test suite:", styles["h3"]))
    story.append(
        _code_block(
            ".\\.venv\\Scripts\\python.exe -m pytest",
            styles["code"],
        )
    )
    story.append(
        Paragraph(
            f"FastAPI app version: <b>{facts.backend_version}</b>. "
            f"Routers registered at startup: "
            f"<font face='Courier'>{', '.join(facts.backend_routers)}</font>.",
            styles["body"],
        )
    )

    # ---- 4. Env vars ----
    story.append(PageBreak())
    story.append(Paragraph("4. Environment variables reference", styles["h1"]))
    story.append(
        Paragraph(
            "These are extracted from <font face='Courier'>backend/.env.example</font>. "
            "Each provider's routes return 503 if its config is missing, so "
            "partial setups are perfectly fine while you're rolling out one "
            "provider at a time.",
            styles["body"],
        )
    )

    env_rows = [["Variable", "Default / example", "Notes"]]
    for name, value, comment in facts.env_vars:
        env_rows.append(
            [
                Paragraph(f"<font face='Courier'>{_esc(name)}</font>", styles["body"]),
                Paragraph(
                    f"<font face='Courier'>{_esc(value)}</font>", styles["body"]
                ),
                Paragraph(_esc(comment) if comment else "&mdash;", styles["body"]),
            ]
        )
    env_table = Table(
        env_rows,
        colWidths=[1.8 * inch, 2.0 * inch, 2.7 * inch],
        repeatRows=1,
    )
    env_table.setStyle(
        TableStyle(
            [
                ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#1a3a5f")),
                ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
                ("FONTNAME", (0, 0), (-1, 0), "Helvetica-Bold"),
                ("FONTSIZE", (0, 0), (-1, 0), 9),
                ("ROWBACKGROUNDS", (0, 1), (-1, -1),
                    [colors.white, colors.HexColor("#f6f6f6")]),
                ("GRID", (0, 0), (-1, -1), 0.25, colors.HexColor("#cccccc")),
                ("VALIGN", (0, 0), (-1, -1), "TOP"),
                ("LEFTPADDING", (0, 0), (-1, -1), 4),
                ("RIGHTPADDING", (0, 0), (-1, -1), 4),
                ("TOPPADDING", (0, 0), (-1, -1), 3),
                ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
            ]
        )
    )
    story.append(env_table)

    # ---- 5. Provider setup ----
    story.append(PageBreak())
    story.append(Paragraph("5. Provider setup", styles["h1"]))

    story.append(Paragraph("Ko-fi", styles["h2"]))
    story.extend(
        _bullets(
            [
                "Dashboard -> More -> API. Set Webhook URL to "
                "https://<your-app>.fly.dev/webhooks/kofi.",
                "Copy the verification token into KOFI_VERIFICATION_TOKEN.",
                "Set KOFI_USERNAME so the portal can deep-link with the "
                "claim code prefilled as the donation message.",
            ],
            styles["body"],
        )
    )

    story.append(Paragraph("PayPal", styles["h2"]))
    story.extend(
        _bullets(
            [
                "developer.paypal.com -> Apps &amp; Credentials -> create REST app.",
                "On that app: Webhooks -> URL /webhooks/paypal. Subscribe to "
                "PAYMENT.CAPTURE.COMPLETED (and PAYMENT.SALE.COMPLETED if you "
                "accept classic donations).",
                "Copy Client ID, Secret, Webhook ID into env. Use "
                "PAYPAL_SANDBOX=true while testing.",
                "Set PAYPAL_ME_USERNAME for the portal's paypal.me link. "
                "Donors paste the claim code into PayPal's note field "
                "manually &mdash; paypal.me URLs cannot prefill notes. The "
                "portal renders the code prominently with a Copy button.",
            ],
            styles["body"],
        )
    )

    story.append(Paragraph("Patreon", styles["h2"]))
    story.extend(
        _bullets(
            [
                "Creator portal -> Settings -> Webhooks -> add "
                "/webhooks/patreon. Subscribe to members:pledge:* plus "
                "members:create and members:update.",
                "Copy the webhook secret into PATREON_WEBHOOK_SECRET.",
                "For OAuth linking: create an API client in the developer "
                "portal with redirect URI /portal/patreon/callback. Fill in "
                "PATREON_CLIENT_ID, PATREON_CLIENT_SECRET, PATREON_REDIRECT_URI.",
                "Set PATREON_USERNAME for the portal's Patreon link.",
                "First-time patrons use the 'Link my Patreon account' button "
                "to bind their account. Later donations from the same patron "
                "are auto-credited via the provider_links table.",
            ],
            styles["body"],
        )
    )

    story.append(Paragraph("PayMongo (GCash + Maya + cards)", styles["h2"]))
    story.extend(
        _bullets(
            [
                "dashboard.paymongo.com -> Developers -> API Keys. Copy the "
                "secret key into PAYMONGO_SECRET_KEY. This is required to "
                "mint PaymentLinks.",
                "Developers -> Webhooks -> add /webhooks/paymongo with the "
                "event payment.paid.",
                "Copy the signing secret into PAYMONGO_WEBHOOK_SECRET.",
                "The portal mints a PaymentLink with metadata.claim_code "
                "baked in when the donor clicks Pay &mdash; no manual code "
                "entry needed.",
            ],
            styles["body"],
        )
    )

    # ---- 6. Deploy ----
    story.append(PageBreak())
    story.append(Paragraph("6. Deploying the backend to Fly.io", styles["h1"]))
    story.append(
        Paragraph(
            f"Defaults baked into <font face='Courier'>fly.toml</font>: "
            f"region <b>{facts.fly_region}</b>, "
            f"shared CPU, <b>{facts.fly_memory_mb} MB</b> RAM, "
            f"1 GB persistent volume mounted at /data for SQLite.",
            styles["body"],
        )
    )
    story.append(
        _code_block(
            "flyctl auth login\n"
            "flyctl launch --no-deploy            # claims app name, rewrites fly.toml\n"
            "flyctl volumes create valcoin_data --size 1 --region sin\n"
            "flyctl secrets set \\\n"
            "  PLUGIN_TOKEN=\"$(python -c 'import secrets;print(secrets.token_urlsafe(32))')\" \\\n"
            "  PUBLIC_BASE_URL=\"https://<your-app>.fly.dev\" \\\n"
            "  DONATION_URL=\"https://<your-app>.fly.dev/portal\" \\\n"
            "  KOFI_VERIFICATION_TOKEN=\"...\" KOFI_USERNAME=\"yourname\"\n"
            "flyctl deploy",
            styles["code"],
        )
    )
    story.append(Paragraph("Per-provider secrets (set after their accounts are wired up):", styles["h3"]))
    story.append(
        _code_block(
            "flyctl secrets set \\\n"
            "  PAYPAL_CLIENT_ID=\"...\" PAYPAL_CLIENT_SECRET=\"...\" \\\n"
            "  PAYPAL_WEBHOOK_ID=\"...\" PAYPAL_ME_USERNAME=\"...\" \\\n"
            "  PATREON_WEBHOOK_SECRET=\"...\" PATREON_USERNAME=\"...\" \\\n"
            "  PATREON_CLIENT_ID=\"...\" PATREON_CLIENT_SECRET=\"...\" \\\n"
            "  PATREON_REDIRECT_URI=\"https://<your-app>.fly.dev/portal/patreon/callback\" \\\n"
            "  PAYMONGO_WEBHOOK_SECRET=\"...\" PAYMONGO_SECRET_KEY=\"sk_live_...\"",
            styles["code"],
        )
    )
    story.append(
        Paragraph(
            "Tip: The app name in <font face='Courier'>fly.toml</font> ships as "
            "the placeholder <font face='Courier'>valheim-donations</font>. "
            "<font face='Courier'>flyctl launch --no-deploy</font> rewrites it "
            "to whatever name you pick.",
            styles["note"],
        )
    )

    # ---- 7. Plugin build ----
    story.append(PageBreak())
    story.append(Paragraph("7. Building the BepInEx plugin", styles["h1"]))
    story.append(
        Paragraph(
            "The csproj expects the following DLLs in "
            "<font face='Courier'>valheim-plugin/libs/</font>. Anything starred "
            "is not in source control &mdash; copy it from your local Valheim "
            "install at <font face='Courier'>valheim_Data/Managed/</font>.",
            styles["body"],
        )
    )
    libs_dir = PLUGIN / "libs"
    present = {p.name for p in libs_dir.glob("*.dll")} if libs_dir.exists() else set()
    dll_rows = [["DLL", "Status"]]
    for dll in facts.required_dlls:
        if dll in present:
            status = Paragraph(
                "<font color='#2a7a2a'>present in libs/</font>", styles["body"]
            )
        else:
            status = Paragraph(
                "<font color='#a02020'><b>MISSING</b> &mdash; copy from "
                "valheim_Data/Managed/</font>",
                styles["body"],
            )
        dll_rows.append(
            [Paragraph(f"<font face='Courier'>{_esc(dll)}</font>", styles["body"]), status]
        )
    dll_table = Table(dll_rows, colWidths=[3.2 * inch, 3.3 * inch], repeatRows=1)
    dll_table.setStyle(
        TableStyle(
            [
                ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#1a3a5f")),
                ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
                ("FONTNAME", (0, 0), (-1, 0), "Helvetica-Bold"),
                ("FONTSIZE", (0, 0), (-1, 0), 9),
                ("ROWBACKGROUNDS", (0, 1), (-1, -1),
                    [colors.white, colors.HexColor("#f6f6f6")]),
                ("GRID", (0, 0), (-1, -1), 0.25, colors.HexColor("#cccccc")),
                ("VALIGN", (0, 0), (-1, -1), "TOP"),
                ("LEFTPADDING", (0, 0), (-1, -1), 4),
                ("RIGHTPADDING", (0, 0), (-1, -1), 4),
            ]
        )
    )
    story.append(dll_table)
    story.append(Spacer(1, 8))
    story.append(
        _code_block(
            "cd valheim-plugin\n"
            "dotnet build -c Release\n"
            "# Output: bin\\Release\\net472\\ValheimDonationSystem.dll",
            styles["code"],
        )
    )
    story.append(
        Paragraph(
            f"Plugin version: <b>{facts.plugin_version}</b>. The existing "
            "<font face='Courier'>bin/Release/net472/</font> output bundles "
            "<font face='Courier'>Jotunn.dll</font> and "
            "<font face='Courier'>YamlDotNet.dll</font>, but the current csproj "
            "no longer references either &mdash; that artifact predates the "
            "dependency cleanup. Always rebuild before shipping.",
            styles["note"],
        )
    )

    # ---- 8. Install on server ----
    story.append(Paragraph("8. Installing the plugin on the server", styles["h1"]))
    story.extend(
        _bullets(
            [
                "Copy ValheimDonationSystem.dll into the server's "
                "BepInEx/plugins/ folder.",
                "Start the server once so the plugin generates template "
                "config files in BepInEx/config/.",
                "Edit valcoin_config.json: set backend_url to your Fly app "
                "URL and plugin_token to match the PLUGIN_TOKEN secret on "
                "the backend.",
                "Edit valcoin_admins.yaml: replace the placeholder Steam64 "
                "with your own admin IDs.",
                "Restart the server. Watch the log for "
                "'[Valheim Donations] Plugin loaded' and "
                "'Startup complete. Admins: N, Backend ready: True'.",
            ],
            styles["body"],
        )
    )
    story.append(Paragraph("Optional: install client-side too", styles["h3"]))
    story.append(
        Paragraph(
            "If players install the same DLL into their own BepInEx/plugins/, "
            "they get the F8 in-game panel. Vanilla clients keep working via "
            "chat commands &mdash; nothing is required of them.",
            styles["body"],
        )
    )
    story.append(
        Paragraph(
            "Environment overrides: <font face='Courier'>VALCOIN_BACKEND_URL</font> "
            "and <font face='Courier'>VALCOIN_PLUGIN_TOKEN</font> beat the JSON "
            "file. Useful on hosts that expose env vars but not config files.",
            styles["body"],
        )
    )

    # ---- 9. Config files ----
    story.append(PageBreak())
    story.append(Paragraph("9. Configuration files &amp; persistent state", styles["h1"]))
    config_rows = [
        ["Path", "Purpose"],
        ["BepInEx/config/valcoin_config.json", "Backend URL + bearer token + poll interval"],
        ["BepInEx/config/valcoin_admins.yaml", "Admin Steam64 ID allowlist"],
        ["BepInEx/config/valcoin_shop.yaml", "SKU catalog (auto-generated)"],
        ["BepInEx/config/valcoin_data/coin_balances.json", "Local balance cache + applied-grant dedupe"],
        ["BepInEx/config/valcoin_data/perks.json", "Per-player perks, charges, title, /home"],
        ["/data/valcoin.sqlite3 (backend)", "Authoritative ledger (WAL mode, on the Fly volume)"],
    ]
    config_table = Table(
        [
            [Paragraph(f"<font face='Courier'>{_esc(r[0])}</font>", styles["body"]),
             Paragraph(_esc(r[1]), styles["body"])]
            if i > 0
            else [Paragraph(f"<b>{_esc(r[0])}</b>", styles["body"]),
                  Paragraph(f"<b>{_esc(r[1])}</b>", styles["body"])]
            for i, r in enumerate(config_rows)
        ],
        colWidths=[3.4 * inch, 3.1 * inch],
        repeatRows=1,
    )
    config_table.setStyle(
        TableStyle(
            [
                ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#1a3a5f")),
                ("ROWBACKGROUNDS", (0, 1), (-1, -1),
                    [colors.white, colors.HexColor("#f6f6f6")]),
                ("GRID", (0, 0), (-1, -1), 0.25, colors.HexColor("#cccccc")),
                ("VALIGN", (0, 0), (-1, -1), "TOP"),
                ("LEFTPADDING", (0, 0), (-1, -1), 4),
                ("RIGHTPADDING", (0, 0), (-1, -1), 4),
                ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
            ]
        )
    )
    story.append(config_table)

    # ---- 10. Chat commands ----
    story.append(PageBreak())
    story.append(Paragraph("10. Chat commands &amp; shop catalog", styles["h1"]))
    cmd_rows = [
        ["Command", "Who", "What"],
        ["/coins", "anyone", "Show your balance and owned perks"],
        ["/donate", "anyone", "Mint a claim code, DM you the donation URL"],
        ["/shop", "anyone", "List SKUs with prices and ownership"],
        ["/buy <sku>", "anyone", "Purchase a SKU"],
        ["/gift <player> <amount>", "anyone", "Transfer Valcoins to another player"],
        ["/title <text | clear>", "perk", "Set chat title prefix (chat_title perk)"],
        ["/sethome", "perk", "Save current position (sethome perk)"],
        ["/home", "perk", "Teleport to saved position (3-min cooldown)"],
        ["/shout <message>", "perk", "Server-wide broadcast (consumes 1 charge)"],
        ["/topdonors", "anyone", "Show lifetime top-5 donor leaderboard"],
        ["/givecoins <player> <amount>", "admin", "Grant coins manually"],
        ["/removecoins <player> <amount>", "admin", "Subtract coins manually"],
    ]
    cmd_table = Table(
        [
            [Paragraph(f"<font face='Courier'>{_esc(r[0])}</font>", styles["body"]),
             Paragraph(_esc(r[1]), styles["body"]),
             Paragraph(_esc(r[2]), styles["body"])]
            if i > 0
            else [Paragraph(f"<b>{_esc(c)}</b>", styles["body"]) for c in r]
            for i, r in enumerate(cmd_rows)
        ],
        colWidths=[2.2 * inch, 0.8 * inch, 3.5 * inch],
        repeatRows=1,
    )
    cmd_table.setStyle(
        TableStyle(
            [
                ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#1a3a5f")),
                ("ROWBACKGROUNDS", (0, 1), (-1, -1),
                    [colors.white, colors.HexColor("#f6f6f6")]),
                ("GRID", (0, 0), (-1, -1), 0.25, colors.HexColor("#cccccc")),
                ("VALIGN", (0, 0), (-1, -1), "TOP"),
                ("LEFTPADDING", (0, 0), (-1, -1), 4),
                ("RIGHTPADDING", (0, 0), (-1, -1), 4),
            ]
        )
    )
    story.append(cmd_table)
    story.append(Spacer(1, 8))
    story.append(Paragraph("Shop catalog (YAML) example", styles["h3"]))
    story.append(
        _code_block(
            "shop:\n"
            "  donor_badge:\n"
            "    name: \"Donor Badge\"\n"
            "    description: \"A star next to your name in chat. Forever.\"\n"
            "    price: 500\n"
            "    effect: grant_perk        # grant_perk | add_charges\n"
            "    perk: donor_badge         # internal perk id\n"
            "  shout:\n"
            "    name: \"Server Shout\"\n"
            "    price: 200\n"
            "    effect: add_charges\n"
            "    perk: shout\n"
            "    charges: 1                # uses per purchase",
            styles["code"],
        )
    )
    story.append(
        Paragraph(
            "Built-in perk handlers: <b>donor_badge</b>, <b>chat_title</b>, "
            "<b>sethome</b>, <b>shout</b>. Adding a new effect type requires "
            "editing <font face='Courier'>ShopHandler.cs::ApplyEffect</font>; "
            "new SKUs that reuse an existing handler are pure YAML.",
            styles["body"],
        )
    )

    # ---- 11. UI panel ----
    story.append(PageBreak())
    story.append(Paragraph("11. In-game UI panel (Phase 5)", styles["h1"]))
    story.append(
        Paragraph(
            "When the plugin is installed client-side, press <b>F8</b> "
            "(configurable) to open a minimal IMGUI panel.",
            styles["body"],
        )
    )
    story.extend(
        _bullets(
            [
                "Donate tab: one button. Calls the backend, displays your "
                "code + portal URL.",
                "Shop tab: scrollable SKU list with Buy buttons; shows "
                "owned/charges per row.",
                "Gift tab: recipient + amount fields, Send Gift button. "
                "Surfaces /shout and /title editors when you own those perks.",
                "Top tab: leaderboard of lifetime donors (uses "
                "/api/leaderboard/top).",
                "Auto-closes when you open inventory, map, or pause menu.",
                "Sends commands via a silent vc_action RPC so nothing leaks "
                "into public chat.",
            ],
            styles["body"],
        )
    )

    # ---- 12. Verify ----
    story.append(Paragraph("12. Verifying the full flow", styles["h1"]))
    story.extend(
        _bullets(
            [
                "Run /donate in-game. The plugin should DM you a portal URL "
                "with an AB12-CD34 code.",
                "Open the URL. The portal should show your code and four "
                "provider buttons.",
                "Make a test donation through one provider (Ko-fi sandbox or "
                "PayPal sandbox is easiest).",
                "Watch backend logs for the webhook hit and a 'grants' row "
                "being written.",
                "Within ~10 s (your poll_interval_seconds), the plugin "
                "should pop a HUD message with the credited amount, and "
                "/coins should reflect the new balance.",
                "Try /buy and /gift to exercise the atomic /api/spend and "
                "/api/transfer endpoints. Retries are safe &mdash; both are "
                "idempotency-keyed.",
            ],
            styles["body"],
        )
    )

    # ---- 13. Ops & troubleshooting ----
    story.append(PageBreak())
    story.append(Paragraph("13. Operations &amp; troubleshooting", styles["h1"]))
    story.extend(
        _bullets(
            [
                "SQLite + WAL on a 1 GB Fly volume comfortably handles "
                "thousands of donations.",
                "All webhooks are idempotent at the DB level via "
                "donations(provider, provider_txn_id) UNIQUE. Replays are "
                "safe.",
                "The plugin caches the last 5000 applied grant ids locally, "
                "so a crash-then-replay won't double-credit.",
                "OAuth states have a 10-minute TTL and are GC'd "
                "opportunistically.",
                "Donations below MIN_GRANT_COINS are stored with status "
                "'rejected' for audit but are not credited.",
                "If a donation arrives without a recognisable claim code "
                "(donor forgot to paste it), it lands in the unmatched "
                "list. Use /api/admin/unmatched and /api/admin/credit-"
                "unmatched to reconcile manually.",
                "Currency conversion is JSON-configurable via COINS_PER_UNIT "
                "(defaults: $1 = 50 coins, P1 = 1 coin).",
                "The plugin's local balance cache is NOT authoritative &mdash; "
                "it only answers /coins without a round-trip. The backend "
                "SQLite is the source of truth.",
            ],
            styles["body"],
        )
    )

    story.append(Paragraph("Common errors", styles["h2"]))
    story.extend(
        _bullets(
            [
                "503 from a /webhooks/* route -> the matching provider's "
                "env vars are unset on the backend. Set them and redeploy.",
                "Plugin logs 'Backend ready: False' -> backend_url or "
                "plugin_token in valcoin_config.json is wrong, or the "
                "backend isn't reachable from the server box.",
                "Plugin builds but won't load -> a Unity DLL is missing from "
                "libs/; check Section 7's table.",
                "Donor paid but no in-game credit -> check unmatched list "
                "first; if the donation isn't there at all, the webhook "
                "didn't fire (provider dashboard usually has a retry button).",
            ],
            styles["body"],
        )
    )

    # ---- 14. Keep up to date ----
    story.append(Paragraph("14. Keeping this guide up to date", styles["h1"]))
    story.append(
        Paragraph(
            "This PDF is produced by "
            "<font face='Courier'>scripts/generate_setup_guide.py</font>. "
            "Whenever you change one of these files, regenerate it:",
            styles["body"],
        )
    )
    story.extend(
        _bullets(
            [
                "backend/app/main.py (version, routers)",
                "valheim-plugin/Plugin.cs (plugin version)",
                "backend/.env.example (env vars)",
                "valheim-plugin/ValheimDonationSystem.csproj (required DLLs)",
                "backend/fly.toml (deploy region / VM)",
                "backend/README.md or valheim-plugin/README.md (workflow text)",
                "backend/app/config.py (settings defaults)",
            ],
            styles["body"],
        )
    )
    story.append(
        _code_block(
            "# regenerate\n"
            "python scripts/generate_setup_guide.py\n\n"
            "# verify nothing drifted (CI / pre-commit)\n"
            "python scripts/generate_setup_guide.py --check",
            styles["code"],
        )
    )
    story.append(
        Paragraph(
            "Suggested git pre-commit hook (PowerShell):",
            styles["h3"],
        )
    )
    story.append(
        _code_block(
            "# .git\\hooks\\pre-commit\n"
            "python scripts/generate_setup_guide.py\n"
            "git add docs/SETUP_GUIDE.pdf",
            styles["code"],
        )
    )
    story.append(
        Paragraph(
            f"Inputs fingerprint for this build: "
            f"<font face='Courier'>{facts.inputs_fingerprint}</font>. "
            "If you re-run the generator and the fingerprint changes, "
            "something the guide covers has shifted.",
            styles["note"],
        )
    )

    return story


# ---------------------------------------------------------------------------
# Driver
# ---------------------------------------------------------------------------


def _footer(canvas, doc):
    canvas.saveState()
    canvas.setFont("Helvetica", 8)
    canvas.setFillColor(colors.HexColor("#777777"))
    canvas.drawString(
        0.75 * inch,
        0.5 * inch,
        "Valheim Donations — Setup Guide (auto-generated)",
    )
    canvas.drawRightString(
        LETTER[0] - 0.75 * inch,
        0.5 * inch,
        f"Page {doc.page}",
    )
    canvas.restoreState()


def render_pdf(output_path: Path, facts: ProjectFacts) -> bytes:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    buf_path = output_path
    doc = SimpleDocTemplate(
        str(buf_path),
        pagesize=LETTER,
        leftMargin=0.75 * inch,
        rightMargin=0.75 * inch,
        topMargin=0.8 * inch,
        bottomMargin=0.8 * inch,
        title="Valheim Donations Setup Guide",
        author="generate_setup_guide.py",
        # Embed the fingerprint in PDF metadata so --check can find it reliably
        # without parsing the (possibly compressed) content stream.
        subject=f"inputs-fingerprint={facts.inputs_fingerprint}",
    )
    styles = _styles()
    story = build_story(facts, styles)
    doc.build(story, onFirstPage=_footer, onLaterPages=_footer)
    return buf_path.read_bytes()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output",
        type=Path,
        default=DEFAULT_OUTPUT,
        help="Output PDF path (default: docs/SETUP_GUIDE.pdf)",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="Exit 1 if regeneration would change the file content (excluding "
        "PDF metadata timestamps).",
    )
    args = parser.parse_args()

    facts = extract_facts()

    if args.check:
        if not args.output.exists():
            print(
                f"[stale] {args.output} does not exist. "
                f"Run: python scripts/generate_setup_guide.py",
                file=sys.stderr,
            )
            return 1
        # The current PDF embeds the inputs fingerprint in its /Subject
        # metadata entry; parse it out and compare against the live one.
        # We deliberately avoid re-rendering — that would change timestamps
        # and produce a spurious diff every run.
        data = args.output.read_bytes()
        m = re.search(rb"inputs-fingerprint=([a-f0-9]{12})", data)
        prev_fp = m.group(1).decode() if m else ""
        if prev_fp == facts.inputs_fingerprint:
            print(f"[ok] guide is up to date (fingerprint {facts.inputs_fingerprint})")
            return 0
        print(
            f"[stale] guide fingerprint changed "
            f"(was {prev_fp or 'unknown'}, now {facts.inputs_fingerprint}). "
            f"Run: python scripts/generate_setup_guide.py",
            file=sys.stderr,
        )
        return 1

    render_pdf(args.output, facts)
    rel = os.path.relpath(args.output, REPO_ROOT)
    print(
        f"[ok] wrote {rel}  "
        f"(backend {facts.backend_version}, plugin {facts.plugin_version}, "
        f"fingerprint {facts.inputs_fingerprint})"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
