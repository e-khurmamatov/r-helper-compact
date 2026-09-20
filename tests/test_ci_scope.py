import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("ci_scope", Path(__file__).resolve().parents[1] / "scripts/ci_scope.py")
scope = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scope)


class CiScopeTests(unittest.TestCase):
    def test_documentation_only(self):
        self.assertFalse(scope.requires_checks(["README.md", "README.ru.md", "devices/README.md", "assets/screenshots/overview.png"]))

    def test_code_and_packaging_require_checks_even_with_docs(self):
        for path in ["controller/src/main.rs", "winui/MainWindow.xaml", "Cargo.lock", ".github/workflows/build-windows.yml", "LICENSE", "licenses/THIRD_PARTY_LICENSES.md", "assets/rhelper.svg", "devices/laptops/028c.json"]:
            with self.subTest(path=path):
                self.assertTrue(scope.requires_checks(["README.md", path]))

    def test_empty_manual_and_scheduled_runs_require_checks(self):
        self.assertTrue(scope.requires_checks([]))
        for event in ["workflow_dispatch", "schedule"]:
            self.assertEqual(scope.changed_paths(event, {}), [])

    @patch.object(scope.subprocess, "check_output")
    def test_pr_compares_merge_base_and_keeps_deleted_source_paths(self, command):
        command.side_effect = ["ancestor\n", b"controller/old.rs\0docs/new.md\0"]
        paths = scope.changed_paths("pull_request", {"pull_request": {"base": {"sha": "base"}, "head": {"sha": "head"}}})
        self.assertTrue(scope.requires_checks(paths))
        self.assertEqual(command.call_args.args[0], ["git", "diff", "--name-only", "--no-renames", "-z", "ancestor", "head", "--"])

    @patch.object(scope.subprocess, "check_output", return_value=b"README.md\0")
    def test_push_compares_entire_push(self, command):
        self.assertFalse(scope.requires_checks(scope.changed_paths("push", {"before": "before", "after": "after"})))
        self.assertEqual(command.call_args.args[0][-3:], ["before", "after", "--"])


if __name__ == "__main__":
    unittest.main()
