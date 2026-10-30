# Screenshots

The PNGs here are renders of the real screens (English, light theme) made by the headless Avalonia tests
`ScreenshotTests`, `SessionScreenshotTests` and `Phase3ScreenshotTests` in `tests/Murmur.App.Tests`.

A normal test run writes them to `artifacts/screenshots/` (untracked), so it never changes the files in this folder.
To refresh the tracked ones, run the tests with `MURMUR_UPDATE_SCREENSHOTS=1`:

```bash
MURMUR_UPDATE_SCREENSHOTS=1 dotnet test tests/Murmur.App.Tests -c Release --filter "FullyQualifiedName~ScreenshotTests"
```

Look at the result before committing: none of the images may be empty or broken.
