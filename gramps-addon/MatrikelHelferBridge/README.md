# MatrikelHelfer Bridge — Gramps addon

Gramps addon that exposes a local HTTP/JSON API so MatrikelHelfer can read the
open family tree and (from stage 6 on) write sources/citations into it.
Specification: `docs/MatrikelHelfer-Gramps-Bridge-Anforderungen.md` in the
main repo. Current state: **stages 1–6** (skeleton, gramplet UI, server +
marshalling, `/ping`, discovery file, token, security checks, person search
and detail, source/repository search, `POST /capture` with idempotency).

## Files

| File | Purpose |
|---|---|
| `MatrikelHelferBridge.gpr.py` | Registration (adjust `gramps_target_version` on Gramps ≠ 6.0) |
| `MatrikelHelferBridge.py` | Gramplet: status display + start/stop, new-token, port controls |
| `mhbridge_service.py` | Server lifecycle, `run_in_main` marshalling, `/api/v1/ping`, discovery file, token, request ring buffer |

## Automated tests

```powershell
powershell -ExecutionPolicy Bypass -File gramps-addon\tests\run_tests.ps1
```

Runs 26 integration tests headless inside the real Gramps runtime
(`grampsd.exe` CLI + a test tool plugin): auth/origin/content-type checks,
person search and detail, source/repository lookup, full capture,
source reuse, idempotent replay, rollback on bad target, undo. Uses the
dedicated **MHBridgeTest** tree (wiped and reseeded every run — the tool
refuses any other tree), a separate port (8811) and a temp discovery file,
so a running production bridge is unaffected. Gramps GUI must be closed
(tree lock). The manual checks below remain relevant only for
UI-level behavior (gramplet display, editor-dialog interplay).

## Install (development loop)

Copy this folder to `%APPDATA%\gramps\gramps60\plugins\MatrikelHelferBridge\`,
restart Gramps, add the **MatrikelHelfer Bridge** gramplet on the Dashboard.
The server starts automatically when the gramplet loads.

## Stage 1–3 verification

```powershell
# discovery file exists and has port/token/pid (FA-3)
Get-Content "$env:APPDATA\gramps\matrikelhelfer\endpoint.json"

$ep = Get-Content "$env:APPDATA\gramps\matrikelhelfer\endpoint.json" | ConvertFrom-Json

# ping without token works (5.3)
curl.exe -s "http://127.0.0.1:$($ep.port)/api/v1/ping"

# authenticated request passes token check -> 404 NOT_FOUND (no endpoints yet)
curl.exe -s -H "X-MatrikelHelfer-Token: $($ep.token)" "http://127.0.0.1:$($ep.port)/api/v1/persons"

# wrong token -> 401 UNAUTHORIZED (T-08)
curl.exe -s -H "X-MatrikelHelfer-Token: wrong" "http://127.0.0.1:$($ep.port)/api/v1/persons"

# Origin header -> 403 ORIGIN_FORBIDDEN (T-09 / NFA-3)
curl.exe -s -H "Origin: https://evil.example" "http://127.0.0.1:$($ep.port)/api/v1/ping"
```

## Stage 4 verification (person search)

```powershell
$ep = Get-Content "$env:APPDATA\gramps\matrikelhelfer\endpoint.json" | ConvertFrom-Json
$H = @{ "X-MatrikelHelfer-Token" = $ep.token }
$base = "http://127.0.0.1:$($ep.port)/api/v1"

# free search over all names (first call builds the index - check the
# Gramps log for "person index built: N persons in X ms")
Invoke-RestMethod -Headers $H "$base/persons?q=test" | ConvertTo-Json -Depth 5

# targeted: surname + birth-year window + result paging
Invoke-RestMethod -Headers $H "$base/persons?surname=test&birth_year_from=1700&birth_year_to=1800&limit=10"

# place filter (matches any event place of the person)
Invoke-RestMethod -Headers $H "$base/persons?place=pollenfeld"

# detail view: take a handle from a search result
Invoke-RestMethod -Headers $H "$base/persons/<handle>" | ConvertTo-Json -Depth 6

# unknown handle -> 404 NOT_FOUND
curl.exe -s -H "X-MatrikelHelfer-Token: $($ep.token)" "$base/persons/doesnotexist"
```

Index invalidation check: edit any person in Gramps (e.g. change a given
name), search again — the log shows a fresh "person index built" line and the
result reflects the edit.

## Stage 5 verification (sources/repositories)

```powershell
$ep = Get-Content "$env:APPDATA\gramps\matrikelhelfer\endpoint.json" | ConvertFrom-Json
$H = @{ "X-MatrikelHelfer-Token" = $ep.token }
$base = "http://127.0.0.1:$($ep.port)/api/v1"

# title search; the spike's writes should show up as "Spike source #NNN"
Invoke-RestMethod -Headers $H "$base/sources?q=spike" | ConvertTo-Json -Depth 6

# dedup lookup by attribute (spec 7.2) - spike sources carry MH_SourceKey
Invoke-RestMethod -Headers $H "$base/sources?attribute_key=MH_SourceKey&attribute_value=spike-0001"

# attribute_key without attribute_value -> 400 INVALID_REQUEST
curl.exe -s -H "X-MatrikelHelfer-Token: $($ep.token)" "$base/sources?attribute_key=MH_SourceKey"

# repository search
Invoke-RestMethod -Headers $H "$base/repositories?q=" | ConvertTo-Json -Depth 5
```

## Stage 6 verification (POST /capture)

Use the **test tree**. Pick a person first, then run a full capture:

```powershell
$ep = Get-Content "$env:APPDATA\gramps\matrikelhelfer\endpoint.json" | ConvertFrom-Json
$H = @{ "X-MatrikelHelfer-Token" = $ep.token; "Content-Type" = "application/json" }
$base = "http://127.0.0.1:$($ep.port)/api/v1"

$p = (Invoke-RestMethod -Headers $H "$base/persons?limit=1").results[0]
$p.primary_name   # make sure this is who you think it is

$body = @{
  request_id = [guid]::NewGuid().ToString()
  repository = @{
    match = @{ by = "name"; value = "Matricula Online" }
    create_if_missing = @{ name = "Matricula Online"; type = "Website"
                           url = "https://data.matricula-online.eu/" }
  }
  source = @{
    match = @{ by = "attribute"; key = "MH_SourceKey"; value = "test-taufen-001" }
    create_if_missing = @{
      title = "Testpfarrei, Taufbuch Bd. 1 (1770-1790)"
      author = "Kath. Pfarramt Testpfarrei"
      attributes = @(@{ key = "MH_SourceKey"; value = "test-taufen-001" })
      repository_ref = @{ call_number = "T 1/1"; media_type = "Book" }
    }
  }
  citation = @{
    page = "S. 42, Eintrag 7"
    date = @{ type = "regular"; year = 1780; month = 3; day = 12 }
    confidence = "normal"
    attributes = @(@{ key = "MH_Permalink"; value = "https://data.matricula-online.eu/de/..." })
    notes = @(@{ type = "Citation"; text = "Transkription: Joannes, ehel. Sohn des ..." })
  }
  create_event_if_missing = @{
    person_handle = $p.handle
    event_type = "Baptism"
    role = "Primary"
    date = @{ type = "regular"; year = 1780; month = 3; day = 12 }
    description = "Taufe laut Matrikel"
  }
} | ConvertTo-Json -Depth 8

$r = Invoke-RestMethod -Headers $H -Method Post -Uri "$base/capture" -Body $body
$r | ConvertTo-Json -Depth 6
```

Checklist (maps to T-02…T-06 in the spec):

1. In Gramps: the person has a new Baptism event with the citation attached;
   the source hangs under the Matricula repository; the note and the
   `MH_Permalink` attribute are on the citation.
2. **Replay**: send the exact same `$body` again → identical response,
   *no* new objects (FA-7). A *new* `request_id` with the same source key
   → new citation but `source.was_existing: true` (T-04).
3. **Undo** (`Ctrl+Z`) once → the whole capture disappears together;
   the undo entry is labeled `MatrikelHelfer: Zitat …` (T-06).
4. **Target instead of event creation**: rerun with
   `targets = @(@{ type = "event"; handle = "<event handle>" })` (take a
   handle from `GET /persons/<handle>` events) and without
   `create_event_if_missing` → citation lands on the existing event (T-02).
5. Bad handle in `targets` → 404, and *nothing* was created (T-11).


Also check: close/switch the tree → `/ping` reports `tree_open`/new
`session_id`; **Stop/Start** in the gramplet rewrites the discovery file with a
fresh token; quitting Gramps deletes the discovery file and frees the port.
