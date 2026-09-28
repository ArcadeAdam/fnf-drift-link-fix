#!/usr/bin/env python3
"""Apply or restore the narrowly scoped Fast & Furious Drift link spawn fix.

Only the exact supported executable is accepted. No game files are distributed.
Python 3.10+; standard library only.
"""

import argparse
from contextlib import contextmanager
from dataclasses import dataclass
import hashlib
import os
from pathlib import Path
import stat
import sys
import tempfile


class PatchError(Exception):
    """An expected refusal or filesystem failure, suitable for a CLI message."""


@dataclass(frozen=True)
class PatchSpec:
    size: int
    offset: int
    before: bytes
    after: bytes
    original_sha256: str
    fixed_sha256: str

    def __post_init__(self):
        if (self.offset < 0 or not self.before or self.before == self.after
                or len(self.before) != len(self.after)
                or self.offset + len(self.before) > self.size):
            raise ValueError('Invalid patch bounds or byte sequences')
        for digest in (self.original_sha256, self.fixed_sha256):
            if len(digest) != 64 or any(c not in '0123456789abcdef' for c in digest):
                raise ValueError('SHA256 values must be 64 lowercase hex characters')
        if self.original_sha256 == self.fixed_sha256:
            raise ValueError('Original and fixed hashes must differ')


SUPPORTED = PatchSpec(
    size=69_480_448,
    offset=0x86C5B,
    before=bytes.fromhex('be0000000090'),
    after=bytes.fromhex('8bf190909090'),
    original_sha256='749733fcd3c8314aa1deb08b8c7eb6106444f66bb937e7ae47d3fab89eec7171',
    fixed_sha256='c6d739f0d7d244d7f228ed33e34961016025abadd1e7ec95b450d2e66b513f56',
)
BACKUP_SUFFIX = '.fnf-drift-link-fix.original.bak'


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def identify(data, spec=SUPPORTED):
    """Return original/fixed; never accept merely matching bytes at the offset."""
    if len(data) != spec.size:
        raise PatchError(f'Unsupported file size: {len(data):,} bytes; expected {spec.size:,}.')
    digest = sha256(data)
    region = data[spec.offset:spec.offset + len(spec.before)]
    if digest == spec.original_sha256 and region == spec.before:
        return 'original'
    if digest == spec.fixed_sha256 and region == spec.after:
        return 'fixed'
    raise PatchError(f'Unsupported executable SHA256: {digest}. No changes made.')


def target_path(path):
    path = Path(path).expanduser()
    if path.is_symlink():
        raise PatchError('Refusing a symbolic-link target; select the actual executable.')
    try:
        path = path.resolve(strict=True)
    except OSError as exc:
        raise PatchError(f'Cannot find executable: {path}: {exc}') from exc
    if not path.is_file():
        raise PatchError(f'Target is not a regular file: {path}')
    return path


def backup_path(path):
    return path.with_name(path.name + BACKUP_SUFFIX)


@contextmanager
def locked_target(path):
    """Hold a read/write exclusion guard while preparing atomic replacement.

    Windows shares no reads, writes, or deletes. Requesting write access also
    refuses an executable mapped by a running game. The handle is released at
    the final commit boundary because Windows rename requires it to be closed.
    POSIX uses an advisory exclusive lock; the CLI is intended primarily for Windows.
    """
    if os.name == 'nt':
        import ctypes
        from ctypes import wintypes
        import msvcrt

        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD,
                                      wintypes.DWORD, ctypes.c_void_p,
                                      wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        kernel.CreateFileW.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.CloseHandle.restype = wintypes.BOOL
        handle = kernel.CreateFileW(str(path), 0xC0000000, 0, None, 3, 0x80, None)
        if handle == ctypes.c_void_p(-1).value:
            error = ctypes.get_last_error()
            raise PatchError('Cannot exclusively open the executable for writing. '
                             'Close the game and launcher, and check file permissions. '
                             f'Windows error {error}.')
        try:
            descriptor = msvcrt.open_osfhandle(handle, os.O_RDWR | os.O_BINARY)
        except BaseException:
            kernel.CloseHandle(handle)
            raise
        with os.fdopen(descriptor, 'r+b') as stream:
            yield stream
    else:
        import fcntl

        with path.open('r+b') as stream:
            try:
                fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            except OSError as exc:
                raise PatchError('Executable is in use; close the game and launcher.') from exc
            try:
                yield stream
            finally:
                fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def write_and_sync(stream, data):
    stream.write(data)
    stream.flush()
    os.fsync(stream.fileno())


def verified_backup(path, spec):
    if path.is_symlink():
        raise PatchError(f'Refusing a symbolic-link backup: {path}')
    try:
        data = path.read_bytes()
    except OSError as exc:
        raise PatchError(f'Cannot read original backup: {path}: {exc}') from exc
    try:
        state = identify(data, spec)
    except PatchError as exc:
        raise PatchError(f'Backup is not the verified original; it will not be overwritten: {path}') from exc
    if state != 'original':
        raise PatchError(f'Backup contains the fixed file, not the original: {path}')
    return data


def ensure_backup(path, data, spec):
    """Exclusive creation means an existing backup is never overwritten."""
    try:
        with path.open('xb') as stream:
            write_and_sync(stream, data)
    except FileExistsError:
        pass
    except OSError as exc:
        raise PatchError(f'Could not finish creating backup: {path}. The target is unchanged. '
                         'An incomplete backup may remain; preserve or rename it before retrying. '
                         f'{exc}') from exc
    verified_backup(path, spec)


def atomic_replace(path, data, expected_state, spec, source_stream, source_data):
    """Verify a same-directory temporary file before replacing the target."""
    descriptor, temporary_name = tempfile.mkstemp(prefix='.' + path.name + '.fnf-drift-',
                                                  suffix='.tmp', dir=str(path.parent))
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, 'wb') as stream:
            write_and_sync(stream, data)
        os.chmod(temporary, stat.S_IMODE(os.fstat(source_stream.fileno()).st_mode))
        if identify(temporary.read_bytes(), spec) != expected_state:
            raise PatchError('Temporary output failed verification; target is unchanged.')
        # A writer ignoring a POSIX advisory lock must not silently redirect or
        # change the input while a backup is being made.
        current = path.stat()
        opened = os.fstat(source_stream.fileno())
        if (current.st_dev, current.st_ino) != (opened.st_dev, opened.st_ino):
            raise PatchError('Target was replaced during the operation; refusing to continue.')
        source_stream.seek(0)
        if source_stream.read() != source_data:
            raise PatchError('Target changed during the operation; refusing to continue.')
        if os.name == 'nt':
            # MoveFileEx cannot replace a destination while this exclusion handle
            # is held. Release only after every verification, immediately before
            # the atomic rename. Keep the game/launcher closed throughout.
            source_stream.close()
        os.replace(temporary, path)
        if identify(path.read_bytes(), spec) != expected_state:
            raise PatchError('Final verification failed; keep the original backup for recovery.')
    finally:
        # A normal exception leaves the target intact and removes our scratch file.
        # A forcibly killed process may leave a .tmp file, never a partial target.
        temporary.unlink(missing_ok=True)


def check(path, spec=SUPPORTED):
    path = target_path(path)
    return identify(path.read_bytes(), spec)


def apply(path, spec=SUPPORTED):
    path = target_path(path)
    with locked_target(path) as stream:
        original = stream.read()
        if identify(original, spec) == 'fixed':
            return 'already fixed'
        fixed = original[:spec.offset] + spec.after + original[spec.offset + len(spec.before):]
        if identify(fixed, spec) != 'fixed':
            raise PatchError('Computed patch failed verification; target is unchanged.')
        ensure_backup(backup_path(path), original, spec)
        atomic_replace(path, fixed, 'fixed', spec, stream, original)
    return 'applied'


def restore(path, spec=SUPPORTED):
    path = target_path(path)
    with locked_target(path) as stream:
        fixed = stream.read()
        if identify(fixed, spec) != 'fixed':
            raise PatchError('Restore requires the known fixed executable; this file is already original.')
        original = verified_backup(backup_path(path), spec)
        atomic_replace(path, original, 'original', spec, stream, fixed)
    return 'restored'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('check', 'apply', 'restore'))
    parser.add_argument('executable', type=Path, help='Path to your legally obtained sdaemon.exe')
    args = parser.parse_args(argv)
    try:
        result = {'check': check, 'apply': apply, 'restore': restore}[args.command](args.executable)
    except (PatchError, OSError) as exc:
        print(f'Error: {exc}', file=sys.stderr)
        return 1
    print(f'{result.capitalize()}: {args.executable}')
    if args.command == 'apply' and result == 'applied':
        print(f'Original backup: {backup_path(args.executable)}')
    elif args.command == 'restore':
        print('The original backup has been retained.')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
