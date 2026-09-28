"""Small synthetic fixtures only; no proprietary game files are needed."""
import contextlib
import io
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

import patch_drift as patch


class PatcherTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / 'sdaemon.exe'
        self.before = bytes.fromhex('be0000000090')
        self.after = bytes.fromhex('8bf190909090')
        self.offset = 123
        self.original = bytes(range(123)) + self.before + bytes(range(255, 20, -1))
        self.fixed = self.original[:123] + self.after + self.original[129:]
        self.spec = patch.PatchSpec(len(self.original), self.offset, self.before, self.after,
                                    patch.sha256(self.original), patch.sha256(self.fixed))
        self.path.write_bytes(self.original)
        self.backup = patch.backup_path(self.path)

    def test_apply_only_changes_patch_region_and_restore_recovers_original(self):
        self.assertEqual(patch.check(self.path, self.spec), 'original')
        self.assertEqual(patch.apply(self.path, self.spec), 'applied')
        changed = self.path.read_bytes()
        self.assertEqual(changed, self.fixed)
        self.assertEqual(changed[:self.offset], self.original[:self.offset])
        self.assertEqual(changed[self.offset + 6:], self.original[self.offset + 6:])
        self.assertEqual(self.backup.read_bytes(), self.original)
        self.assertEqual(patch.check(self.path, self.spec), 'fixed')
        self.assertEqual(patch.restore(self.path, self.spec), 'restored')
        self.assertEqual(self.path.read_bytes(), self.original)
        self.assertEqual(self.backup.read_bytes(), self.original)

    def test_second_apply_is_a_no_op(self):
        patch.apply(self.path, self.spec)
        target_time = self.path.stat().st_mtime_ns
        backup_time = self.backup.stat().st_mtime_ns
        with mock.patch.object(patch, 'atomic_replace', side_effect=AssertionError('must not rewrite')):
            self.assertEqual(patch.apply(self.path, self.spec), 'already fixed')
        self.assertEqual(self.path.stat().st_mtime_ns, target_time)
        self.assertEqual(self.backup.stat().st_mtime_ns, backup_time)

    def test_unknown_hash_rejected_even_when_patch_bytes_match(self):
        unknown = b'X' + self.original[1:]
        self.path.write_bytes(unknown)
        for operation in (patch.check, patch.apply, patch.restore):
            with self.assertRaisesRegex(patch.PatchError, 'Unsupported executable SHA256'):
                operation(self.path, self.spec)
        self.assertEqual(self.path.read_bytes(), unknown)
        self.assertFalse(self.backup.exists())

    def test_wrong_size_rejected_without_creating_backup(self):
        self.path.write_bytes(self.original[:-1])
        with self.assertRaisesRegex(patch.PatchError, 'Unsupported file size'):
            patch.apply(self.path, self.spec)
        self.assertFalse(self.backup.exists())

    def test_existing_unrelated_backup_is_never_overwritten(self):
        unrelated = b'important unrelated backup'
        self.backup.write_bytes(unrelated)
        with self.assertRaisesRegex(patch.PatchError, 'will not be overwritten'):
            patch.apply(self.path, self.spec)
        self.assertEqual(self.backup.read_bytes(), unrelated)
        self.assertEqual(self.path.read_bytes(), self.original)

    def test_existing_valid_backup_reused_without_overwriting(self):
        self.backup.write_bytes(self.original)
        backup_time = self.backup.stat().st_mtime_ns
        patch.apply(self.path, self.spec)
        self.assertEqual(self.backup.stat().st_mtime_ns, backup_time)
        self.assertEqual(self.backup.read_bytes(), self.original)

    def test_restore_requires_fixed_target_and_original_backup(self):
        with self.assertRaisesRegex(patch.PatchError, 'already original'):
            patch.restore(self.path, self.spec)
        self.path.write_bytes(self.fixed)
        with self.assertRaisesRegex(patch.PatchError, 'Cannot read original backup'):
            patch.restore(self.path, self.spec)
        self.backup.write_bytes(self.fixed)
        with self.assertRaisesRegex(patch.PatchError, 'not the original'):
            patch.restore(self.path, self.spec)
        self.assertEqual(self.path.read_bytes(), self.fixed)

    def test_failed_atomic_replace_keeps_original_and_verified_backup(self):
        with mock.patch.object(patch.os, 'replace', side_effect=OSError('simulated interruption')):
            with self.assertRaisesRegex(OSError, 'simulated interruption'):
                patch.apply(self.path, self.spec)
        self.assertEqual(self.path.read_bytes(), self.original)
        self.assertEqual(self.backup.read_bytes(), self.original)
        self.assertEqual(list(self.path.parent.glob('*.tmp')), [])
        self.assertEqual(patch.apply(self.path, self.spec), 'applied')

    def test_failed_restore_replace_keeps_fixed_and_original_backup(self):
        patch.apply(self.path, self.spec)
        with mock.patch.object(patch.os, 'replace', side_effect=OSError('simulated interruption')):
            with self.assertRaises(OSError):
                patch.restore(self.path, self.spec)
        self.assertEqual(self.path.read_bytes(), self.fixed)
        self.assertEqual(self.backup.read_bytes(), self.original)
        self.assertEqual(patch.restore(self.path, self.spec), 'restored')

    def test_interrupted_backup_write_leaves_target_unchanged(self):
        def interrupted(stream, data):
            stream.write(data[:17])
            raise OSError('simulated full disk')

        with mock.patch.object(patch, 'write_and_sync', side_effect=interrupted):
            with self.assertRaisesRegex(patch.PatchError, 'target is unchanged'):
                patch.apply(self.path, self.spec)
        self.assertEqual(self.path.read_bytes(), self.original)
        self.assertEqual(self.backup.read_bytes(), self.original[:17])
        with self.assertRaisesRegex(patch.PatchError, 'will not be overwritten'):
            patch.apply(self.path, self.spec)

    def test_interrupted_temporary_write_leaves_verified_backup_and_original(self):
        self.backup.write_bytes(self.original)

        def interrupted(stream, data):
            stream.write(data[:17])
            raise OSError('simulated full disk')

        with mock.patch.object(patch, 'write_and_sync', side_effect=interrupted):
            with self.assertRaises(OSError):
                patch.apply(self.path, self.spec)
        self.assertEqual(self.path.read_bytes(), self.original)
        self.assertEqual(self.backup.read_bytes(), self.original)
        self.assertEqual(list(self.path.parent.glob('*.tmp')), [])

    def test_fixed_apply_without_backup_is_no_op_and_restore_still_requires_backup(self):
        self.path.write_bytes(self.fixed)
        self.assertEqual(patch.apply(self.path, self.spec), 'already fixed')
        self.assertFalse(self.backup.exists())
        with self.assertRaises(patch.PatchError):
            patch.restore(self.path, self.spec)

    def test_cli_unknown_file_is_friendly_failure(self):
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            self.assertEqual(patch.main(['check', str(self.path)]), 1)
        self.assertIn('Error: Unsupported file size', stderr.getvalue())
        self.assertNotIn('Traceback', stderr.getvalue())

    @unittest.skipUnless(os.name == 'nt', 'Windows sharing semantics')
    def test_windows_guard_refuses_a_running_executable(self):
        # The current Python executable is already mapped by this test process.
        # The guard only requests a handle; this test never writes to that file.
        with self.assertRaisesRegex(patch.PatchError, 'Close the game and launcher'):
            with patch.locked_target(Path(sys.executable).resolve()):
                self.fail('A running executable unexpectedly allowed exclusive write access')

    @unittest.skipUnless(os.name == 'nt', 'Windows sharing semantics')
    def test_windows_guard_blocks_other_readers_until_released(self):
        with patch.locked_target(self.path):
            with self.assertRaises(OSError):
                self.path.read_bytes()
        self.assertEqual(self.path.read_bytes(), self.original)


if __name__ == '__main__':
    unittest.main()
