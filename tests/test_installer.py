"""Packaging invariants; actual MSI schema/ICE validation is performed by WiX."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
NS = {"w": "http://schemas.microsoft.com/wix/2006/wi"}


class InstallerChecks(unittest.TestCase):
    def setUp(self):
        self.xml = ET.parse(ROOT / "installer/Product.wxs")

    def test_machine_install_and_stable_upgrade_identity(self):
        package = self.xml.find(".//w:Package", NS)
        self.assertEqual(package.attrib["InstallScope"], "perMachine")
        self.assertEqual(package.attrib["Platform"], "x64")
        self.assertIsNotNone(self.xml.find(".//w:Directory[@Id='ProgramFiles64Folder']/w:Directory[@Id='INSTALLFOLDER']", NS))
        self.assertEqual(self.xml.find(".//w:Product", NS).attrib["UpgradeCode"], "C253AC71-2DEE-449E-BF85-6783B5C4D74F")
        self.assertEqual(self.xml.find(".//w:MajorUpgrade", NS).attrib["Schedule"], "afterInstallInitialize")

    def test_installer_never_launches_controller_or_enables_startup(self):
        actions = self.xml.findall(".//w:CustomAction", NS)
        self.assertEqual(len(actions), 1)
        self.assertIn('reg.exe', actions[0].attrib["ExeCommand"])
        self.assertIn('/v "R-Helper Compact"', actions[0].attrib["ExeCommand"])
        condition = self.xml.find(".//w:Custom[@Action='RemoveCurrentUserStartup']", NS).text
        self.assertEqual(condition, 'REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE')
        self.assertEqual(self.xml.findall(".//w:RegistryValue", NS), [])
        self.assertEqual(self.xml.findall(".//w:RemoveRegistryKey", NS), [])

    def test_payload_includes_notices_and_installer_is_validated(self):
        files = {e.attrib["Id"] for e in self.xml.findall(".//w:File", NS)}
        self.assertTrue({"CompactExe", "License", "CompactNotices", "Readme"} <= files)
        script = (ROOT / "scripts/Build-Installer.ps1").read_text(encoding="utf-8")
        self.assertIn("6ac824e1642d6f7277d0ed7ea09411a508f6116ba6fae0aa5f2c7daa2ff43d31", script)
        self.assertNotIn("-sval", script)
        self.assertNotIn("-sice", script)
        self.assertIn("$LASTEXITCODE -ne 0", script)


if __name__ == "__main__":
    unittest.main()
