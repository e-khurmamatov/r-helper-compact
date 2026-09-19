# Translations

`en.json` contains the English source strings. Copy it to a Windows culture name such as `de.json`, `fr.json` or `pt-BR.json`, then translate the values only. Keep keys and format placeholders (`{0}`, `{1:0}`, `{0:X2}`) unchanged.

Files are embedded automatically. The app tries the Windows display culture, then its parent language, then English. Missing or empty translations fall back to English. No hardware changes or language registration code are needed.

Run `python -m unittest discover -s tests -v`. After building, preview without a controller:

```powershell
.\rhelper-compact.exe --ui-smoke-test --ui-culture=de-DE
```

The culture override only works in UI smoke mode. Native Windows controls use the selected language where supported; low-level diagnostic details may remain in English.
