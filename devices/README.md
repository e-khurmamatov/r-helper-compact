# Laptop registry

Each file in `laptops/` describes one Razer laptop. The registry is compiled into the controller; installed apps do not load arbitrary device definitions.

To propose a model:

1. Copy `templates/laptop.json` to `laptops/XXXX.json`, using the built-in controller's four-digit USB PID.
2. Fill in the name, PID, exact chassis SKU prefixes (`RZ09-` plus four digits), source and notes. Do not include serial numbers.
3. Keep `enabled: false` and `verification: "candidate"`. Submit a PR with BIOS/EC versions, diagnostics and protocol evidence.

A maintainer reviews compatibility before enabling writes. A PID match alone is insufficient: the SKU must also match exactly one enabled record. Unsupported or ambiguous matches are blocked before HID is opened.

Supported protocol adapters are `Legacy4` and `Modern6`; keyboard effects use `none` or `standard_matrix_ff`. New protocol families require Rust changes and hardware tests. JSON cannot contain arbitrary commands.

Blade 15 (2023), PID 029C, is disabled pending resolution of the 029C/029E discrepancy. Other inherited profiles are not proof that every feature has been tested.

The app's support form opens a draft [GitHub issue](https://github.com/e-khurmamatov/r-helper-compact/issues/new?template=device_support.md). Forks can change `RepositoryUrl` in `winui/RHelper.Compact.csproj`.
