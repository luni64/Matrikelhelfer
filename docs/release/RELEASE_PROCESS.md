# Release process

Step-by-step process for releasing Matrikelhelfer version X.Y.Z. Commands
are PowerShell from the repo root; `gh` is the GitHub CLI (on `PATH`).

## 1. Version bump (two places)

- `Matrikelhelfer\Matrikelhelfer.csproj` → `<Version>X.Y.Z</Version>`
- `installer\setup.iss` → `#define MyAppVersion "X.Y.Z"`

## 2. Roll the release docs

Sources: the entries collected in `docs/release/NEXT_RELEASE.md` during development.

1. **`installer\WHATS_NEW.template`** — fill in the `New`/`Fixed` sections
   from `NEXT_RELEASE.md`; keep the `${VERSION}` placeholder in the title
   line (substituted at installer compile time).
2. **`CHANGELOG.md`** — insert a new `## X.Y.Z - YYYY-MM-DD` section at the
   top with the same content.
3. **Reset `docs/release/NEXT_RELEASE.md`** to the empty skeleton
   (`# Next Release` / `## Features` / `## Bug Fixes`).

## 3. Build the binaries

```powershell
Remove-Item Matrikelhelfer\bin\Release -Recurse -Force        # clean slate
dotnet publish Matrikelhelfer\Matrikelhelfer.csproj -c Release
```

Output: `Matrikelhelfer\bin\Release\net8.0-windows\win-x64\publish`. The csproj pins
`RuntimeIdentifier=win-x64`, so the output should contain only win-x64
natives — if a `runtimes\` folder with other RIDs appears, something regressed.

Smoke test: start `publish\Matrikelhelfer.exe`, confirm it detects a Matricula page, close it.

## 4. Release ZIP (portable version)

Flat archive of the publish folder (no top-level directory):

```powershell
Compress-Archive -Path Matrikelhelfer\bin\Release\net8.0-windows\win-x64\publish\* `
                 -DestinationPath Matrikelhelfer_vX_Y_Z.zip -Force
```

Build it in a temp/scratch location — it must not be committed.

## 5. Signed installer

`setup.iss` signs via the Inno Setup sign-tool profile **`certum`** (already
configured in the Inno IDE on this machine — same profile used for AutoNum).
IDE builds sign automatically; for command-line builds pass the command via
`/S`. The signtool path must be converted to its 8.3 short path, because an
embedded quoted path breaks ISCC's argument parsing:

```powershell
$st  = (Get-ItemProperty "HKCU:\Software\Jordan Russell\Inno Setup\SignTools").SignTool0
$cmd = $st.Substring($st.IndexOf('=') + 1)
if ($cmd -match '^"([^"]+)"\s+(.*)$') {
    $fso = New-Object -ComObject Scripting.FileSystemObject
    $cmd = "$($fso.GetFile($Matches[1]).ShortPath) $($Matches[2])"
}
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /Q "/Scertum=$cmd" installer\setup.iss
```

Output: `installer\bin\Matrikelhelfer-X.Y.Z-Setup.exe`. Verify the signature:

```powershell
(Get-AuthenticodeSignature installer\bin\Matrikelhelfer-X.Y.Z-Setup.exe).Status   # must be "Valid"
```

Note: if the Certum key needs a PIN, a prompt may appear during signing.

## 6. Commit, push, draft release

1. Commit the doc/version changes and push to `main`.
2. Create a **draft** release (tag is only created when published, so nothing
   is public yet). The repo is private, so the release is only visible to
   accounts with repo access regardless of draft state:

```powershell
gh release create vX.Y.Z --repo luni64/Matrikelhelfer --draft --target main `
    --title "vX.Y.Z" --notes-file <body.md> `
    installer\bin\Matrikelhelfer-X.Y.Z-Setup.exe Matrikelhelfer_vX_Y_Z.zip
```

Release body = the `WHATS_NEW` content plus an *Installation* section naming
both downloads (installer vs. portable ZIP).

## 7. User testing, then publish

Test both binaries from the draft page (installer **and** portable ZIP).
Fixes go to `main`; rebuild and replace assets with
`gh release upload vX.Y.Z --clobber <files>`.

On the go-ahead:

```powershell
gh release edit vX.Y.Z --repo luni64/Matrikelhelfer --draft=false --latest
```

This creates the tag and makes the release "Latest".
