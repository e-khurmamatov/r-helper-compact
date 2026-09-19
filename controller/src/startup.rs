#[cfg(windows)]
const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";

#[cfg(windows)]
const APP_REGISTRY_NAME: &str = "R-Helper Compact";

#[cfg(windows)]
pub fn is_startup_enabled() -> bool {
    use winreg::RegKey;
    use winreg::enums::HKEY_CURRENT_USER;

    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    hkcu.open_subkey(RUN_KEY)
        .and_then(|run| run.get_value::<String, _>(APP_REGISTRY_NAME))
        .map(|path| path == current_exe_path() || path == current_exe_path().trim_matches('"'))
        .unwrap_or(false)
}

#[cfg(not(windows))]
pub fn is_startup_enabled() -> bool {
    false
}

#[cfg(windows)]
pub fn set_startup_enabled(enabled: bool) -> anyhow::Result<()> {
    use winreg::RegKey;
    use winreg::enums::HKEY_CURRENT_USER;

    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let (run, _) = hkcu.create_subkey(RUN_KEY)?;

    if enabled {
        run.set_value(APP_REGISTRY_NAME, &current_exe_path())?;
    } else {
        match run.delete_value(APP_REGISTRY_NAME) {
            Ok(()) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(error.into()),
        }
    }

    Ok(())
}

#[cfg(not(windows))]
pub fn set_startup_enabled(_enabled: bool) -> anyhow::Result<()> {
    Ok(())
}

#[cfg(windows)]
fn current_exe_path() -> String {
    std::env::current_exe()
        .map(|p| {
            if std::env::args().any(|a| a == "--winui-backend") {
                p.with_file_name("rhelper-compact.exe")
            } else {
                p
            }
        })
        .map(|p| format!("\"{}\"", p.to_string_lossy()))
        .unwrap_or_else(|_| "rhelper-compact.exe".to_string())
}
