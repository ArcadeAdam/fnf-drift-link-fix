# FNF Drift linked-race spawn fix

A small, reversible Python patch for one known `sdaemon.exe` build of **The Fast and the Furious Drift**. It corrects a starting-grid bug that can leave the session host's car near the world origin while the other linked player starts correctly.

**The cabinets must already discover each other and join the same race.** This patch addresses vehicle placement at race start. It does not enable LAN, change network settings, or modify preferences or service-menu saves.

No game files are included. You supply your own executable.

## Supported executable

The patcher accepts only these exact file states:

| State | SHA-256 |
| --- | --- |
| Before this fix | `749733fcd3c8314aa1deb08b8c7eb6106444f66bb937e7ae47d3fab89eec7171` |
| After this fix | `c6d739f0d7d244d7f228ed33e34961016025abadd1e7ec95b450d2e66b513f56` |

File size: **69,480,448 bytes**. Other builds and modified executables, including resolution-patched variants, are refused. The hash identifies the supported build; a matching filename is not sufficient.

## Use

Python 3.10 or newer is required; the patcher uses only the standard library. Download this repository or the [latest release](https://github.com/ArcadeAdam/fnf-drift-link-fix/releases/latest) and run the commands from its directory. Replace `PATH_TO_sdaemon.exe` with the path to the executable actually launched by your game loader.

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

Keep the backup. To undo the fix, close the game and launcher and run:

```text
python patch_drift.py restore "PATH_TO_sdaemon.exe"
```

Restore verifies both the fixed executable and its original backup before replacing the executable. It retains the backup.

## What changes

The patch changes six bytes at file offset `0x86C5B`:

```text
Before: BE 00 00 00 00 90    mov esi, 0; nop
After:  8B F1 90 90 90 90    mov esi, ecx; nop; nop; nop; nop
```

The existing code stores every human player into the same temporary starting-grid slot. Each additional human overwrites the preceding one. The replacement uses the existing player-loop index, preserving each human for the game's subsequent grid placement.

This is a derived workaround. The factory instruction at this location is unknown; the replacement is **not** claimed to restore original factory bytes. See [technical notes](docs/technical-notes.md) for the control flow and limitations.

## Validation and scope

Before the fix, the misplaced car followed the host role when the two cabinets exchanged roles. Solo play started normally, and disabling force feedback did not resolve the linked-race problem.

With the fix applied to both cabinets, the user confirmed that both cars started correctly and stayed synchronized in a two-cabinet race with the second cabinet hosting. The patched reverse-host arrangement and races with more than two cabinets have not yet been validated.

This repository does not provide general LAN setup instructions. In particular, adding `-net` alone is not a complete setup procedure for this build: cached machine identity and startup branches can affect network initialization. Preserve a working link configuration while testing this specific correction.

## Patcher tests

Run `python -m unittest discover -s tests -v`. The tests use small synthetic files and cover version refusal, backups, rollback, repeated application, interrupted writes, and Windows file locking. No game files are needed for the test suite.

The patching tool and documentation are released under the [MIT license](LICENSE). That license does not apply to the game or its files.
