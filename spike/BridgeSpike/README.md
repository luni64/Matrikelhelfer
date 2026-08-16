# Bridge Spike — stage-0 write test

Throwaway Gramps addon that decides go/no-go for the MatrikelHelfer bridge
architecture (see `docs/MatrikelHelfer-Gramps-Bridge-Anforderungen.md`, §12.1):
an HTTP server inside the Gramps process, all database access marshalled to the
GTK main thread, one write endpoint that creates Source + Citation and attaches
the citation to a person in a single transaction.

**Use a test tree only.** The spike writes real objects into the open tree.
Create a small test tree (or import a copy) before running anything.

## Install

1. Check your Gramps version under *Help → About*. If it is not 6.0.x, edit
   `gramps_target_version` in `BridgeSpike.gpr.py` to match (e.g. `"6.1"`) —
   a mismatch makes Gramps ignore the addon **silently**.
2. Copy this folder to the Gramps user plugin directory:

   ```
   %APPDATA%\gramps\gramps60\plugins\BridgeSpike\
   ```

   (`gramps61` for Gramps 6.1, etc.)
3. Start Gramps, open the **test tree**.
4. Go to the *Dashboard* view, right-click on empty space → *Add a gramplet* →
   **Bridge Spike**.
5. The gramplet tile should show: `server running on http://127.0.0.1:8791`.

If the tile does not appear or shows a port error, check the Gramps console
output / log for `BridgeSpike` messages.

## Smoke test

In PowerShell (Windows 10 ships `curl.exe`):

```powershell
curl.exe -s http://127.0.0.1:8791/spike/read
```

Expected: `{"tree_open": true, "tree_name": "...", "person_count": N}`

Pick any person ID from the tree (e.g. `I0001`) for the write tests below.

## Test checklist (S-01 … S-09)

All criteria must pass. Keep the Gramps window visible while testing.

**S-01 — single write, immediately visible**

```powershell
curl.exe -s -X POST "http://127.0.0.1:8791/spike/write?gramps_id=I0001"
```

Expected: JSON response with source/citation IDs. In Gramps, *without any
reload*: the Sources view shows `Spike source #001`, the Citations view shows
the citation, and the person's Citations tab lists it.

**S-02 — single undo reverts everything**

Press `Ctrl+Z` once in Gramps. Expected: source, citation, and the link on the
person all disappear together. (Redo `Ctrl+Y` should bring all of it back.)

**S-03 — 50 writes in quick succession**

```powershell
1..50 | ForEach-Object { curl.exe -s -X POST "http://127.0.0.1:8791/spike/write?gramps_id=I0001" }
```

Expected: 50 OK responses, no crash, no hang, UI stays responsive, no visible
memory growth (watch gramps.exe in Task Manager).

**S-04 — two concurrent requests**

PowerShell 7:

```powershell
1..2 | ForEach-Object -Parallel { curl.exe -s -X POST "http://127.0.0.1:8791/spike/write?gramps_id=I0001" }
```

(Windows PowerShell 5.1: run the S-01 command in two terminals at once.)
Expected: both requests answered correctly; execution is serialized.

**S-05 — write while an editor dialog is open**

In Gramps, double-click a person so the person editor dialog is open, then run
the S-01 command. Expected: no crash; either success or a clean error.
Close the dialog afterwards and check the tree state is consistent.

**S-06 — no tree open**

Close the tree (*Family Trees → Close*... in Gramps 6 use the tree manager to
switch/close), then run S-01. Expected:
`{"error": {"code": "NO_TREE_OPEN", ...}}` with HTTP 409, no crash.

**S-07 — tree closed/switched during a running request**

Run the S-03 loop and, while it runs, switch to another tree. Expected: no
crash; requests before the switch land in the old tree, requests after it
either land in the new tree or fail cleanly — no half-written data.

**S-08 — Gramps exit with the server running**

Quit Gramps normally. Expected: process exits cleanly, no zombie process,
port 8791 is free again (`Test-NetConnection 127.0.0.1 -Port 8791` fails).

**S-09 — database check afterwards**

Reopen the test tree, run *Tools → Family Tree Repair → Check and Repair
Database*. Expected: no errors found.

## Cleanup

- Undo the writes (`Ctrl+Z` repeatedly), or simply delete the test tree.
- Remove the `BridgeSpike` folder from the plugin directory and restart Gramps.

## Result

Record pass/fail per criterion in the requirements doc (§12.1). All pass →
architecture confirmed, proceed with stage 1; the `run_in_main` marshalling
layer in `BridgeSpike.py` is the piece to carry over unchanged.
