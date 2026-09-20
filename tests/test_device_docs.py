"""Keep public compatibility tables consistent with compiled identity records."""
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


class DeviceDocumentationTests(unittest.TestCase):
    def test_readmes_list_actual_registry_identities(self):
        records = [json.loads(p.read_text(encoding='utf-8'))
                   for p in (ROOT / 'devices/laptops').glob('*.json')]
        for filename in ('README.md', 'README.ru.md'):
            text = (ROOT / filename).read_text(encoding='utf-8')
            for record in records:
                row = f"| {record['name']} | {record['pid']} | {', '.join(record['sku_prefixes'])} |"
                self.assertIn(row, text, filename)

    def test_unverified_identity_records_cannot_supply_a_command_profile(self):
        for path in (ROOT / 'devices/laptops').glob('*.json'):
            record = json.loads(path.read_text(encoding='utf-8'))
            if record['verification'] == 'candidate':
                self.assertFalse(record['enabled'], path.name)
                self.assertEqual(record['protocol'], 'Unverified', path.name)
                self.assertEqual(record['keyboard_protocol'], 'none', path.name)
