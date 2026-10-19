# Task p4-installer: Windows installer (Inno Setup, per user)

Branch `feat/installer` (based on `phase4`). PLAN.md phase 4: "Інсталятор — Inno Setup, на користувача без прав
адміністратора (D-036); разом із пакуванням `Murmur.Transcription` поруч із застосунком". Read D-036 in
docs/DECISIONS.md and the BACKLOG line about packaging the ASR worker.

## Scope
New `installer/` folder, `scripts/build-installer.ps1`, `scripts/verify-local.ps1` (new `-Installer` step, included in
`-All`), `.github/workflows/ci.yml` (Windows part only), `src/Murmur.Desktop/Murmur.Desktop.csproj` (version/icon
properties only), an app icon file, docs/CI.md (a short section). Not the app's C# code (if the app needs a change,
e.g. a `--version` switch, report it instead).

## Spec
1. **What gets installed**: the Native AOT publish folder of `Murmur.Desktop` for win-x64
   (`dotnet publish src/Murmur.Desktop -c Release -f net11.0-windows10.0.22621.0 -r win-x64`), which already contains
   the AOT-published ASR worker `Murmur.Transcription.exe` and its native libs (sherpa-onnx, onnxruntime) — verify the
   list of files in a real publish on CI and make the installer take the folder as a whole (minus `*.pdb`, `*.dbg`
   and anything else that is not needed at run time — list what you exclude and why). Models and llama.cpp are NOT in
   the installer: the app downloads them (D-031).
2. **Inno Setup script** `installer/Murmur.iss` (Inno Setup 6):
   - `PrivilegesRequired=lowest`, no `PrivilegesRequiredOverridesAllowed` dialog; `DefaultDirName={localappdata}\Programs\Murmur`
     (Inno's `{userpf}` equivalent), `DisableProgramGroupPage=yes`, Start-menu shortcut, optional desktop shortcut
     (unchecked task), "Launch Murmur" on the finish page.
   - Fixed `AppId` GUID (generate once, keep it forever — put a comment saying so), `AppName=Murmur`,
     `AppVersion` passed from the build (`/DAppVersion=...`, default from the csproj `Version`), `AppPublisher` —
     take it from LICENSE / the repo owner name in LICENSE, `ArchitecturesAllowed=x64compatible`,
     `ArchitecturesInstallIn64BitMode=x64compatible`, `MinVersion` matching `$(MurmurWindowsMinVersion)` from
     Directory.Build.props, `WizardStyle=modern`, LZMA2 solid compression, `OutputBaseFilename=Murmur-Setup-<version>`.
   - Languages: Ukrainian and English (Inno ships `Ukrainian.isl`); the wizard picks by the OS UI language.
   - Upgrade over an older install works (same AppId); the app must not be running — use `CloseApplications=yes`
     with `AppMutex` only if the app has a mutex (it does not — then rely on Restart Manager; note it).
   - **Uninstall**: removes the program files and shortcuts. User data (`%LOCALAPPDATA%\Murmur`: recordings, database,
     settings, models — can be many GB) is kept by default; a `[Code]` uninstall page/MsgBox asks
     "Also delete recordings and downloaded models?" (uk/en via `[CustomMessages]`), default No; in silent uninstall it
     is never deleted. If the user chose a recordings folder outside `%LOCALAPPDATA%\Murmur` (settings.json
     `recordingsDirectory`), never touch it — mention it in the message.
   - Icon: the setup and the app use the same `.ico`.
3. **App icon**: there is none yet. Make `installer/murmur.ico` (and set `<ApplicationIcon>` in Murmur.Desktop.csproj
   for the Windows TFM) from the "sphere" of the design (`docs/design/source/*.dc.html`, the recording orb — read
   docs/design/README.md and tokens.json, Violet/Ember are allowed for the orb icon): a simple radial-gradient circle
   is enough. Generate the PNG sizes (16, 24, 32, 48, 64, 128, 256) with a small script (Python + Pillow if available,
   or ImageMagick, or a tiny .NET script — no new NuGet package in the product) and pack them into the .ico; commit the
   script too (`installer/make-icon.*`) so it can be regenerated.
4. **Build script** `scripts/build-installer.ps1 [-Version x.y.z] [-SkipPublish] [-PublishDir ...] [-OutDir artifacts/installer]`:
   publishes (unless skipped), finds `ISCC.exe` (PATH, `${env:ProgramFiles(x86)}\Inno Setup 6`, `$env:ProgramFiles\Inno Setup 6`,
   `$env:LOCALAPPDATA\Programs\Inno Setup 6`); if absent, prints how to install it (`winget install JRSoftware.InnoSetup`)
   and fails. Writes the setup exe and a `.sha256`.
5. **verify-local.ps1 `-Installer`** step: build the installer, install silently into a temp folder
   (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /DIR=<temp>` + `/LOG=`), check `Murmur.exe` and
   `Murmur.Transcription.exe` are there, run the existing startup check against the installed exe (the `-Startup` step
   logic — reuse it), uninstall silently (`unins000.exe /VERYSILENT`), check the folder is gone and user data is kept.
   Add it to the report like the other steps. Follow the script's existing structure and style.
6. **CI** (`ci.yml`, Windows part, runs with the existing Windows condition): after the AOT publish job (reuse its
   artifact instead of publishing again if the workflow already uploads it), a job `installer` that installs Inno Setup
   if `ISCC` is missing (`choco install innosetup -y --no-progress`), builds the installer, runs the same silent
   install → startup → uninstall smoke test (via verify-local.ps1 -Installer -SkipPublish or the same code), and
   uploads `Murmur-Setup-*.exe` as an artifact (retention 14 days). Keep the job's time small (no extra publish).
7. **Docs**: docs/CI.md — how to build the installer locally and where CI puts it.

## Acceptance
Linux: `pwsh` syntax check of the scripts (PowerShell 7 is available at
/tmp/claude-0/-home-user-Murmur/ffa700a1-1873-543f-83db-f4dfe7fce242/scratchpad/pwsh/pwsh — use it to parse the
scripts: `[System.Management.Automation.Language.Parser]::ParseFile`), the solution still builds with 0 warnings,
actionlint-clean workflow if actionlint is available (otherwise careful review). The installer itself and the CI job are
UNVERIFIED until the architect's Windows CI run. Conventional Commits (`build(installer): ...`), no trailers.
