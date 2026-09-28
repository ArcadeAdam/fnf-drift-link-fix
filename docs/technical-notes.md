# Technical notes

## Windows app

The Windows download is a small C# Windows Forms app. It runs on the .NET Framework included with Windows 10 and 11; no Python installation is needed. Its source is in `src/`. It uses the same exact file hashes and six-byte correction as the Python tool.

Build and run its tests from PowerShell:

```powershell
.\build-windows.ps1 -Test
```

The result is `dist/FNFDriftLinkFix.exe`. The build uses the Windows .NET Framework compiler, runs synthetic file tests, and checks that the app's window can initialize without showing it or opening a game file. The app does not request administrator access automatically. Its original backup uses the same filename as the Python tool, so the two tools can share backups.

For supported executable hashes, Python commands, backup and restore behavior, and patcher tests, see [command-line use](command-line.md).

## Observed defect

The supported executable contains a starting-grid routine at virtual address `0x486BE0`. Its human-player pass uses an eight-pointer temporary array.

| Address | Relevant behavior |
| --- | --- |
| `0x486C4E` | Load the player pointer from the player array using `ecx` as the loop index. |
| `0x486C55` | Test player flags bit 0, identifying a human player. |
| `0x486C5B` | Set `esi` to zero. |
| `0x486C61` | Store the human pointer at temporary array index `esi`. |
| `0x486C6A` onward | Compact the non-null human entries and distribute them using the existing starting-grid table. |

The effective insertion is:

```text
for each player index:
    if player is human:
        humans[0] = player
```

With two humans, only the later one survives. The subsequent vehicle-construction pass still creates both vehicles, but its insertion into vacant grid slots explicitly skips humans. It therefore does not recover the overwritten human pointer.

The final grid pass, beginning at `0x486FDB`, applies position and orientation only to pointers present in the temporary array. Position setter `0x4674E0` writes the player's XYZ coordinates and forwards them to the attached vehicle when the player is in the vehicle state. The omitted human consequently misses starting-grid placement.

This explains why a single human receives a normal start, while the first human can be misplaced when another joins. In the observed linked sessions, the affected first human was the session host, independent of which physical cabinet hosted.

## Correction

For the supported file, virtual address `0x486C5B` corresponds to file offset `0x86C5B`.

| Bytes | Meaning |
| --- | --- |
| `BE 00 00 00 00 90` | `mov esi, 0` followed by one NOP |
| `8B F1 90 90 90 90` | `mov esi, ecx` followed by four NOPs |

The corrected insertion is:

```text
for each player index:
    if player is human:
        humans[player index] = player
```

Each human retains a distinct entry. The original compaction, human count, placement table, and position/orientation calls then run unchanged. For one human, the placement table uses grid slot 5; for two humans, it uses slots 5 and 6.

The replacement has the same six-byte length, preserves instruction fallthrough and processor flags, and uses the loop index already held in `ecx`. The store is the only following use of `esi` before the loop proceeds. This avoids introducing a new dependency on a player-structure field.

The temporary array and the later player/grid passes have eight slots. The correction assumes the existing enumeration remains within the game's normal eight-player bound. Every possible producer of the initial loop-count globals has not been proved in this analysis. It also chooses player enumeration order, which may differ from an unknown factory ordering policy.

## Provenance and supported build

The factory instruction at this location has not been established. Multiple preexisting copies of this build, including a copy labelled “Original” in a resolution-patch collection, contained the same zero-index instruction. Such labels do not establish that an executable is an untouched factory image.

The reviewed public OpenParrot Drift implementation does not patch this address. This observation does not establish who introduced the instruction or why. The correction is a newly derived workaround, not a recovery of known factory bytes.

The patcher restricts changes to the exact supported file size, SHA-256, and byte sequence. It does not search for similar instructions in other builds or apply the change at an assumed address after a hash mismatch. Only the six specified bytes change; executable length remains unchanged.

## Validation

The investigation established the following before the correction:

- The two cabinets could discover each other and establish a linked race.
- The misplaced car followed the host role when the cabinets exchanged host/client roles.
- Solo play started normally.
- Network traffic remained active and the observed game tick rate was approximately 60 ticks per second.
- The same spawn problem remained with force feedback disabled.

After applying the correction to both executables, the user reported that both cars started correctly and remained synchronized in a two-cabinet race with the second cabinet hosting. The final test restarted both games, switched hosting to the first cabinet, and completed a full race successfully. The second cabinet's capture also recorded it joining as the client and both cars moving. These results validate the tested two-cabinet setup in both hosting orders and persistence across game restarts. More than two cabinets and all game modes remain unvalidated.

## Boundaries

This patch changes starting-grid construction only. It does not modify preferences, machine IDs, link discovery, sockets, frame limiting, input handling, or force feedback. It requires an already-working link setup.

Network startup in this build also involves cached identity and different initialization branches. A command-line `-net` option is not, by itself, a complete or universally safe setup recipe. General network setup and resets are outside this patch's scope.

The original backup is verified before reuse or restoration. Rollback restores the exact supported pre-fix executable and leaves the backup available. Keep both cabinets on matching executable states when testing or reverting.
