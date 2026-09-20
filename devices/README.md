# Laptop registry

Each file in `laptops/` describes one Razer laptop identity. The registry is compiled into the controller; installed apps do not load arbitrary device definitions.

## Identity and control policy

The host must report a recognized Razer manufacturer and an RZ09 chassis SKU through BIOS/SMBIOS. USB vendor 1532 alone is not enough: a peripheral can be attached to another brand of computer. Missing host identity, no identifiable Blade HID, or multiple Blade controller PIDs blocks hardware control.

An exact PID/SKU pair in one enabled registry record supplies a model-specific command profile. Only `user_confirmed` records avoid the experimental label; inherited `upstream_profile` records remain visibly experimental.

Other Razer laptops may use generic `Discovery` when USB product metadata identifies one Blade controller. This includes unregistered identities and disabled/candidate profile records. Generic discovery does not use another model's initialization sequence. Available capabilities come from device responses and are not proof of reliable operation.

## Adding identity evidence

1. Copy `templates/laptop.json` to `laptops/XXXX.json`, using the built-in controller's four-digit USB PID.
2. Fill in name, PID, exact chassis SKU prefixes (`RZ09-` plus four digits), source and notes. Do not include serial numbers.
3. Keep `enabled: false`, `verification: "candidate"` and `protocol: "Unverified"` until a model-specific protocol is established.
4. Submit BIOS/EC versions, reviewed diagnostics and protocol evidence. Enabling a specific profile requires a separate compatibility review.

Supported command adapters are `Legacy4` and `Modern6`; keyboard effects use `none` or `standard_matrix_ff`. `Unverified` is permitted only for disabled candidates and cannot provide commands. New command families require Rust changes and hardware tests. JSON cannot contain arbitrary commands.

Blade 15 (2023), PID 029C, retains a disabled inherited profile because OpenRazer identifies 029E. The separate 029E candidate records that discrepancy without silently replacing the old identity. Neither record proves Compact hardware behavior.

See the compatibility tables in [English](../README.md#laptop-compatibility) and [Russian](../README.ru.md). Each device record contains its identity sources and limitations.

## Diagnostic reports

The status panel exports a local, reviewable text report containing allowlisted identifiers and fixed support-check events. Raw controller logs are excluded. Opening the [GitHub support template](https://github.com/e-khurmamatov/r-helper-compact/issues/new?template=device_support.md) does not put diagnostics into the URL. Users review and attach the file themselves. Forks can change `RepositoryUrl` in `winui/RHelper.Compact.csproj`.


Automatic diagnostic metadata reads only `SystemProductName`, `BIOSVersion`, `ECFirmwareMajorRelease` and `ECFirmwareMinorRelease` from Windows' `HARDWARE\DESCRIPTION\System\BIOS` key, alongside the existing host identity reads. No serial-number, UUID or full SMBIOS dump is requested. A unique chassis catalogue name can prefill the form independently of control authorization; otherwise only a recognized Windows Blade family is retained, without an inferred year. Firmware strings use a bounded numeric-version grammar. Missing, unknown (255) or unrecognized metadata remains unavailable; it does not change the device gate. User corrections are retained in the form and copied request, but the automatic export continues to exclude free text.

References: [Microsoft SMBIOS identity fields](https://learn.microsoft.com/en-us/windows-hardware/drivers/bringup/smbios) and [BIOS/EC firmware properties](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-bios). Collection reads Windows metadata; it does not send device commands.
