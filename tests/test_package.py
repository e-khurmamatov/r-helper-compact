"""Repository and release invariants; hardware behavior is tested separately."""
from pathlib import Path
import re
import tomllib
import unittest

ROOT = Path(__file__).resolve().parents[1]

class PackageChecks(unittest.TestCase):
    def test_workspace_is_self_contained_and_headless(self):
        workspace = tomllib.loads((ROOT / 'Cargo.toml').read_text())['workspace']
        self.assertEqual(set(workspace['members']), {'controller', 'librazer'})
        for member in workspace['members']:
            manifest = tomllib.loads((ROOT / member / 'Cargo.toml').read_text())
            for dependency in manifest['dependencies'].values():
                if isinstance(dependency, dict) and 'path' in dependency:
                    self.assertTrue((ROOT / member / dependency['path'] / 'Cargo.toml').is_file())
        packages = tomllib.loads((ROOT / 'Cargo.lock').read_text())['package']
        self.assertFalse({'eframe', 'egui', 'winit', 'tray-icon', 'wgpu'} & {p['name'] for p in packages})

    def test_provenance_and_original_attribution(self):
        notices = (ROOT / 'THIRD_PARTY_NOTICES.md').read_text(encoding='utf-8')
        self.assertIn('8f4ac7b2bc5b2cd077d73254dddb6873d1a5cab6', notices)
        for name in ['Ivan Romanchuk', 'Robak08', 'Tarek Dakhran', 'tdakhran', 'blauzim']:
            self.assertIn(name, notices)
        self.assertIn('Ivan Romanchuk', (ROOT / 'LICENSE').read_text())
        self.assertIn('Tarek Dakhran', (ROOT / 'licenses/THIRD_PARTY_LICENSES.md').read_text())

    def test_build_does_not_fetch_or_launch_controller(self):
        script = (ROOT / 'scripts/Build.ps1').read_text()
        self.assertIn('cargo test --locked', script)
        self.assertIn('cargo build --release --locked', script)
        for forbidden in ['prepare.ps1', 'work/source', 'git clone', 'Start-Process', '& $Exe', 'LegacyEgui']:
            self.assertNotIn(forbidden, script)

    def test_original_brightness_steps(self):
        source = (ROOT / 'controller/src/lighting.rs').read_text()
        match = re.search(r'BRIGHTNESS_LEVELS: &\[u8\] = &\[([^]]+)\]', source)
        self.assertIsNotNone(match)
        self.assertEqual(list(map(int, re.findall(r'\d+', match[1]))),
                         [0, 13, 28, 43, 59, 74, 89, 105, 120, 133, 148, 163, 179, 194, 209, 225])

    def test_configuration_remains_separate(self):
        source = (ROOT / 'controller/src/config.rs').read_text()
        self.assertIn('join("r-helper-compact")', source)
        self.assertIn('auto_switch_enabled: false', source)

if __name__ == '__main__':
    unittest.main()
