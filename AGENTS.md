# Voicemeeter Bug maintenance

*A pet app by Astra.* Public credit is **Astra**. Keep legal names, employer information, personal paths, credentials, and local diagnostics out of committed files.

Created with **OpenAI Codex (GPT-6), 2026-10-08**. Local repository preparation and diagnostics work: **OpenAI Codex (GPT-6), 2026-10-11**, America/Denver. Record the model, version, and date when making a substantial change.

## Scope and behavior

This is a Windows .NET Framework desktop helper for the supported Voicemeeter Remote API. Manual clicks request an audio-engine restart. Automatic recovery reacts to observed reconnects of selected output devices. It cannot hear distortion, infer that silence is a fault, or prove that accepted restart commands fixed audible sound.

Preserve the normal executable name, existing art, public controls, singleton event names, and hosted integration unless the task calls for changing them. Building or preparing this repository does not authorize changing the running app, restarting audio, enabling startup, or publishing a release.

## Build and checks

- Build from the repository root with `powershell.exe -NoProfile -File .\build-voicemeeter-bug-20261008.ps1`.
- The normal local executable is `voicemeeter-bug-20261008.exe`. Compiled outputs are ignored; keep source and embedded assets in the repository.
- Run `--self-test` for deterministic fake-API recovery checks. Ordinary tests must not call live restart APIs.
- `--repair` affects real audio. `--test-gestures` only suppresses manual repair gestures; it is not an isolated fake-API test harness.
- Record actual results in `pet.json` under `audit.local_verification`. Never replace pending or failed checks with a pass without evidence.
- Compare source changes and test results separately from deployment and from the owner's listening result.

## Privacy and diagnostics

Do not record microphone, call, or playback audio. Structured watcher diagnostics are local JSONL snapshots every five seconds in `%LOCALAPPDATA%\findastra\voicemeeter-bug`.

Keep watcher diagnostics bounded to 2 MiB per file and four files globally across dates: one active plus at most three retained files, including earlier dates and rotations, at most 8 MiB for that set. Before/after manual repair snapshots are diagnostic evidence, not proof of sound quality. Local output device names and identifiers must remain out of commits.

Do not commit incident notes, verification exports, startup backups, previous-version archives, unrelated detector backups, credentials, or absolute personal paths. Retain `.gitignore` coverage and inspect the staged content before a future commit.

## Pet-app conventions

This pet belongs to [Astra's Pet Apps](https://github.com/findastra/astras-pet-apps). Follow the shared [PET-FILES.md](https://github.com/findastra/astras-pet-apps/blob/main/PET-FILES.md) conventions. Submit changes to this pet's name, job, status, audit, and descriptions to the **Friendly Farmer** through dated `cage.submit` metadata. Do not edit generated hub files from this repository.

`pet.json` is this pet's metadata. Its `sprite` and `entry` must identify real repository files. Use `growing` while behavior has known limitations; do not claim approval or completion based on packaging alone. The `audit.cadence` value describes the shared convention and does not itself install a local scheduled task.

Keep existing sprite assets unchanged for code-only tasks. The native app currently uses a subset of its nine embedded mood frames; do not claim complete nine-mood behavior until it is implemented and verified.

## File names and publication

New owner-facing files use lowercase words separated by hyphens and a Denver date immediately before the extension: `description-YYYYMMDD.ext`. When needed, append 24-hour time after the date: `description-YYYYMMDD-HHMMSS.ext`. Existing files are not renamed merely because they use an earlier convention.

Reserved repository filenames used for this preparation are `README.md`, `pet.json`, `AGENTS.md`, `CLAUDE.md`, `.gitignore`, and the shared convention's `LICENSE`. These naming exceptions were surfaced before creation. Other naming exceptions must also be surfaced before a conflicting file is created.

This repository was prepared locally. Do not describe it as published, create a remote repository, push, or change the GitHub topic without authorization for that action. Do not bundle Voicemeeter's separately installed proprietary binaries.

Desktop artwork and size sync: OpenAI Codex (GPT-6), 2026-10-11. Read desktop-pet-20261011.json. No sparkles. Keep draft and approved design status explicit.
