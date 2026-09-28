# Command-line use

The Windows app linked from the [README](../README.md) is the simplest way to apply this fix. The Python command-line tool provides the same six-byte correction for the same supported executable.

## Supported executable

The patcher accepts only these exact file states:

| State | SHA-256 |
| --- | --- |
| Before this fix | `749733fcd3c8314aa1deb08b8c7eb6106444f66bb937e7ae47d3fab89eec7171` |
| After this fix | `c6d739f0d7d244d7f228ed33e34961016025abadd1e7ec95b450d2e66b513f56` |

File size: **69,480,448 bytes**. Other builds and modified executables, including resolution-patched variants, are refused. The hash identifies the supported build; a matching filename is not sufficient.

No game files are included. You supply your own executable. The cabinets must already discover each other and join the same race. This tool does not set up networking or change service-menu settings.

## Apply the fix

Python 3.10 or newer is required. The patcher uses only the standard library. Download this repository and run the commands from its directory. Replace `PATH_TO_sdaemon.exe` with the path to the executable actually launched by your game loader.

1. Check the executable:

   ```text
   python patch_drift.py check "PATH_TO_sdaemon.exe"
   ```

2. Close the game and launcher on each cabinet, then apply the fix to **each cabinet's executable**:

   ```text
   python patch_drift.py apply "PATH_TO_sdaemon.exe"
   ```

   The original is backed up beside the executable as `sdaemon.exe.fnf-drift-link-fix.original.bak`. An existing backup is never overwritten; it must match the expected original to be reused. Applying the patch to an already-fixed executable makes no further changes.

3. Run `check` again on each cabinet, then launch through your existing working link setup and test a linked race. Both cabinets should use the same fixed build.

## Undo the fix

Keep the backup. To restore the executable, close the game and launcher and run:

```text
python patch_drift.py restore "PATH_TO_sdaemon.exe"
```

Restore verifies both the fixed executable and its original backup before replacing the executable. It retains the backup. Keep both cabinets on matching executable states when reverting.

## Run the tests

```text
python -m unittest discover -s tests -v
```

The tests use small synthetic files and cover version refusal, backups, rollback, repeated application, interrupted writes, and Windows file locking. No game files are needed for the test suite.

See the [technical notes](technical-notes.md) for the correction, evidence, and validation limits. The patching tool and documentation are released under the [MIT license](../LICENSE). That license does not apply to the game or its files.
