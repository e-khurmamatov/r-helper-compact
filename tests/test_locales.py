"""Translation files are contributor-editable; validate keys and format arguments."""
import json
from pathlib import Path
import re
import unittest

ROOT=Path(__file__).resolve().parents[1]
LOCALES=ROOT/'winui/Locales'

class LocaleTests(unittest.TestCase):
    def test_unique_keys_and_matching_placeholders(self):
        def unique(pairs):
            result={}
            for key,value in pairs:
                self.assertNotIn(key,result, key)
                self.assertIsInstance(value,str)
                result[key]=value
            return result
        def slots(text):
            return sorted(re.findall(r'(?<!\{)\{(\d+)(?:[^{}]*)\}(?!\})',text))
        english=json.loads((LOCALES/'en.json').read_text(encoding='utf-8'),object_pairs_hook=unique)
        for path in LOCALES.glob('*.json'):
            self.assertRegex(path.stem,r'^[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$')
            translated=json.loads(path.read_text(encoding='utf-8'),object_pairs_hook=unique)
            for key,value in translated.items():
                self.assertIn(key,english,path.name)
                if value.strip():self.assertEqual(slots(english[key]),slots(value),(path.name,key))
    def test_literal_keys_exist_in_english(self):
        english=json.loads((LOCALES/'en.json').read_text(encoding='utf-8'))
        for path in (ROOT/'winui').glob('*.cs'):
            for key in re.findall(r'L\.[TF]\("([^"\\]*)"',path.read_text(encoding='utf-8')):
                self.assertIn(key,english,(path.name,key))
