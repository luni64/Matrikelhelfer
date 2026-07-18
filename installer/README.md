# Matrikelhelfer Installer

## Prerequisites

| Tool | Notes |
|------|-------|
| [Inno Setup 6](https://jrsoftware.org/isinfo.php) | Must be installed to compile `setup.iss` |
| [InnoDependencyInstaller](https://github.com/DomGries/InnoDependencyInstaller) | `CodeDependencies.iss` is vendored in this folder already |

## Build the installer

1. Publish the app in **Release** configuration:

   ```
   dotnet publish Matrikelhelfer\Matrikelhelfer.csproj -c Release
   ```

   The project pins `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`, so the
   output contains only win-x64 native libraries. The release ZIP is created
   from the same folder the installer uses:
   `Matrikelhelfer\bin\Release\net8.0-windows\win-x64\publish`.

2. Open `installer\setup.iss` in the Inno Setup IDE (or compile from the command line):

   ```
   ISCC.exe installer\setup.iss
   ```

3. The resulting installer is written to `installer\bin\Matrikelhelfer-<version>-Setup.exe`.

## Code signing

`setup.iss` is configured to sign both setup EXE and uninstaller using an Inno Setup **Sign Tool profile** named `certum`:

```pascal
SignTool=certum $f
SignedUninstaller=yes
```

This reuses the same `certum` profile already configured in the Inno Setup IDE for the AutoNum project on this machine (Sign Tools are a per-machine IDE setting, not per-project) — nothing to set up here unless building on a different machine. To configure from scratch: **Tools -> Configure Sign Tools...** in the Inno Setup IDE, add a profile named `certum` running your `signtool.exe sign ...` command against your code-signing certificate.

## Version

Before building the installer, update the version number at the top of `setup.iss`:

```pascal
#define MyAppVersion "0.1.0"
```

...and keep it in sync with `<Version>` in `Matrikelhelfer\Matrikelhelfer.csproj`.

## Not yet set up

- **App icon**: no `.ico` exists yet, so the installer/exe use Inno Setup's/`.` default icon. Once one exists, add `ApplicationIcon` to `Matrikelhelfer.csproj` and `SetupIconFile`/an icon `[Files]` entry here, matching how AutoNum does it.
- **License files**: no `LICENSE.txt`/`THIRD_PARTY_LICENCES.md` exist yet (this is a private repo). Add `[Files]` entries copying them into `{app}\licenses` once they exist.
