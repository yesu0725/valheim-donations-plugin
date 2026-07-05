# Development

Local setup and day-to-day workflow for working on either half of the
project.

## Backend

```powershell
cd backend
python -m venv .venv ; .\.venv\Scripts\Activate.ps1
pip install -r requirements-dev.txt
copy .env.example .env   # fill in tokens
uvicorn app.main:app --reload --port 8080
```

Run tests:

```powershell
.\.venv\Scripts\python.exe -m pytest
```

`pytest` is only available inside this venv — the base Python environment
doesn't have it installed. See [BACKEND.md](BACKEND.md) for the full test
layout.

## Plugin

```powershell
cd valheim-plugin
dotnet build -c Release
# → bin\Release\net472\ValheimDonationSystem.dll → drop into BepInEx\plugins\
```

Requires copying three Unity DLLs into `libs/` first — see
[PLUGIN.md](PLUGIN.md) for the full list and source location.

## Keeping the setup guide PDF current

[docs/SETUP_GUIDE.pdf](SETUP_GUIDE.pdf) is generated, not hand-written, from
live project sources (versions, env vars, required DLLs, Fly config).
**Never edit the PDF directly.**

```powershell
python scripts/generate_setup_guide.py
```

Regenerate it whenever you touch any of:

- `backend/app/main.py` (version, routers)
- `valheim-plugin/Plugin.cs` (plugin version)
- `backend/.env.example` (env vars)
- `valheim-plugin/ValheimDonationSystem.csproj` (required DLLs)
- `backend/fly.toml` (deploy region / VM)
- `backend/README.md` or `valheim-plugin/README.md` (workflow text)
- `backend/app/config.py` (settings defaults)

Check whether it's stale without touching the tracked file:

```powershell
python scripts/generate_setup_guide.py --check
```

This compares a SHA-256 fingerprint of the input files against the one
embedded in the current PDF's metadata, and exits non-zero if they've
drifted — suitable for a pre-commit hook or CI step:

```powershell
# .git\hooks\pre-commit
python scripts/generate_setup_guide.py
git add docs/SETUP_GUIDE.pdf
```

## Source of truth

- [scripts/generate_setup_guide.py](../scripts/generate_setup_guide.py) — the generator itself
- [backend/requirements-dev.txt](../backend/requirements-dev.txt) — backend dev dependencies
