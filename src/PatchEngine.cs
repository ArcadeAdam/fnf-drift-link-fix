using System;
using System.IO;
using System.Security.Cryptography;

namespace DriftLinkFix
{
    public enum PatchState { Original, Patched }

    public sealed class PatchException : Exception
    {
        public PatchException(string message) : base(message) { }
        public PatchException(string message, Exception inner) : base(message, inner) { }
    }

    // Internal injection permits small synthetic tests without distributing a game.
    internal sealed class PatchSpec
    {
        internal readonly long Size;
        internal readonly long Offset;
        internal readonly byte[] Before;
        internal readonly byte[] After;
        internal readonly string OriginalHash;
        internal readonly string PatchedHash;

        internal PatchSpec(long size, long offset, byte[] before, byte[] after,
                           string originalHash, string patchedHash)
        {
            if (before == null || after == null || before.Length == 0 ||
                before.Length != after.Length || offset < 0 || size < before.Length ||
                offset > size - before.Length || Equal(before, after) ||
                !ValidHash(originalHash) || !ValidHash(patchedHash) ||
                originalHash == patchedHash)
                throw new ArgumentException("Invalid patch specification.");
            Size = size;
            Offset = offset;
            Before = (byte[])before.Clone();
            After = (byte[])after.Clone();
            OriginalHash = originalHash;
            PatchedHash = patchedHash;
        }

        private static bool ValidHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }

        internal static bool Equal(byte[] first, byte[] second)
        {
            if (first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++)
                if (first[i] != second[i]) return false;
            return true;
        }
    }

    public sealed class PatchEngine
    {
        private const string BackupSuffix = ".fnf-drift-link-fix.original.bak";
        private readonly PatchSpec spec;
        private readonly Action<Stream, Stream> copy;
        private readonly Action<string, string> replace;

        public PatchEngine() : this(new PatchSpec(
            69480448, 0x86c5b,
            new byte[] { 0xbe, 0, 0, 0, 0, 0x90 },
            new byte[] { 0x8b, 0xf1, 0x90, 0x90, 0x90, 0x90 },
            "749733fcd3c8314aa1deb08b8c7eb6106444f66bb937e7ae47d3fab89eec7171",
            "c6d739f0d7d244d7f228ed33e34961016025abadd1e7ec95b450d2e66b513f56")) { }

        internal PatchEngine(PatchSpec specification)
            : this(specification, CopyStream, ReplaceFile) { }

        // Fault-injection seams are internal and only used by the console tests.
        internal PatchEngine(PatchSpec specification, Action<Stream, Stream> copier,
                             Action<string, string> replacer)
        {
            if (specification == null || copier == null || replacer == null)
                throw new ArgumentNullException("Patch dependencies must not be null.");
            spec = specification;
            copy = copier;
            replace = replacer;
        }

        public string GetBackupPath(string path)
        {
            return Friendly(delegate { return FullPath(path) + BackupSuffix; });
        }

        public PatchState Inspect(string path)
        {
            return Friendly(delegate
            {
                string full = CheckedPath(path);
                using (FileStream stream = new FileStream(full, FileMode.Open, FileAccess.Read,
                                                          FileShare.ReadWrite | FileShare.Delete))
                    return Identify(stream);
            });
        }

        public bool CanRestore(string path)
        {
            try
            {
                if (Inspect(path) != PatchState.Patched) return false;
                using (FileStream backup = OpenVerifiedBackup(GetBackupPath(path))) { }
                return true;
            }
            catch (PatchException) { return false; }
        }

        public PatchState Apply(string path)
        {
            return Friendly(delegate
            {
                string full = CheckedPath(path);
                using (FileStream source = OpenExclusive(full))
                {
                    if (Identify(source) == PatchState.Patched) return PatchState.Patched;
                    string backupPath = full + BackupSuffix;
                    CreateBackupIfAbsent(backupPath, source);
                    // Keep a verified backup open without write/delete sharing until commit.
                    using (FileStream backup = OpenVerifiedBackup(backupPath))
                        StageAndReplace(full, source, source, PatchState.Original, PatchState.Patched);
                }
                return PatchState.Patched;
            });
        }

        public PatchState Restore(string path)
        {
            return Friendly(delegate
            {
                string full = CheckedPath(path);
                using (FileStream source = OpenExclusive(full))
                {
                    if (Identify(source) != PatchState.Patched)
                        throw new PatchException("This file is already original. There is nothing to restore.");
                    using (FileStream backup = OpenVerifiedBackup(full + BackupSuffix))
                        StageAndReplace(full, source, backup, PatchState.Patched, PatchState.Original);
                }
                return PatchState.Original;
            });
        }

        private PatchState Identify(Stream stream)
        {
            if (stream.Length != spec.Size)
                throw new PatchException("This game version is not supported. Choose Drift's sdaemon.exe file.");
            string digest;
            stream.Position = 0;
            using (SHA256 hash = SHA256.Create())
                digest = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            byte[] region = new byte[spec.Before.Length];
            stream.Position = spec.Offset;
            int read = 0;
            while (read < region.Length)
            {
                int count = stream.Read(region, read, region.Length - read);
                if (count == 0) throw new PatchException("The executable could not be read completely.");
                read += count;
            }
            stream.Position = 0;
            if (digest == spec.OriginalHash && PatchSpec.Equal(region, spec.Before)) return PatchState.Original;
            if (digest == spec.PatchedHash && PatchSpec.Equal(region, spec.After)) return PatchState.Patched;
            throw new PatchException("This game version is not supported by this fix.");
        }

        private void CreateBackupIfAbsent(string path, FileStream source)
        {
            if (File.Exists(path) || Directory.Exists(path)) return;
            source.Position = 0;
            try
            {
                // CreateNew never truncates an existing file, even if one appears after Exists.
                using (FileStream backup = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                                                          FileShare.None, 81920, FileOptions.WriteThrough))
                {
                    copy(source, backup);
                    backup.Flush(true);
                }
            }
            catch (IOException ex)
            {
                throw new PatchException("Could not finish creating the original backup. The executable is unchanged. " +
                    "An incomplete backup may remain; keep or rename it before trying again.", ex);
            }
            source.Position = 0;
        }

        private FileStream OpenVerifiedBackup(string path)
        {
            if (!File.Exists(path))
                throw new PatchException("The original backup is missing. Expected: " + path);
            CheckRegularFile(path);
            FileStream backup = null;
            try
            {
                backup = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Identify(backup) != PatchState.Original)
                    throw new PatchException("The backup contains a patched file instead of the original.");
                return backup;
            }
            catch (Exception ex)
            {
                if (backup != null) backup.Dispose();
                if (!(ex is PatchException) && !(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
                throw new PatchException("The existing backup is not a readable, verified original. " +
                    "It will not be overwritten. Backup: " + path, ex);
            }
        }

        private void StageAndReplace(string path, FileStream guardedTarget, Stream contents,
                                     PatchState sourceState, PatchState resultState)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(path),
                "." + Path.GetFileName(path) + ".fnf-drift-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                contents.Position = 0;
                using (FileStream staged = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                                                          FileShare.None, 81920, FileOptions.WriteThrough))
                {
                    copy(contents, staged);
                    if (resultState == PatchState.Patched)
                    {
                        staged.Position = spec.Offset;
                        staged.Write(spec.After, 0, spec.After.Length);
                    }
                    staged.Flush(true);
                    if (Identify(staged) != resultState)
                        throw new PatchException("The staged file did not pass verification. The executable is unchanged.");
                }
                if (Identify(guardedTarget) != sourceState)
                    throw new PatchException("The executable changed during preparation. The replacement was cancelled.");
                CheckRegularFile(path);
                // Windows requires the destination handle closed for File.Replace.
                // Keep the game/launcher closed; release only at this final commit boundary.
                guardedTarget.Dispose();
                replace(temporary, path);
                temporary = null;
                if (Inspect(path) != resultState)
                    throw new PatchException("Final verification failed. Keep the original backup for recovery.");
            }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static void CopyStream(Stream source, Stream destination) { source.CopyTo(destination); }

        private static void ReplaceFile(string temporary, string target)
        {
            // Same directory, atomic replacement; no delete-then-move fallback.
            File.Replace(temporary, target, null);
        }

        private static FileStream OpenExclusive(string path)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex)
            {
                throw new PatchException("Close the game and launcher before applying or restoring the fix. " +
                                         "The executable could not be opened exclusively.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new PatchException("Cannot write to the executable. Close the game and launcher, " +
                                         "and check that you have permission to change this file.", ex);
            }
        }

        private static string FullPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new PatchException("Choose your sdaemon.exe file first.");
            return Path.GetFullPath(path);
        }

        private static string CheckedPath(string path)
        {
            string full = FullPath(path);
            if (!File.Exists(full)) throw new PatchException("The selected executable could not be found.");
            CheckRegularFile(full);
            return full;
        }

        private static void CheckRegularFile(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new PatchException("Choose the actual file, not a folder or symbolic link.");
        }

        private static T Friendly<T>(Func<T> action)
        {
            try { return action(); }
            catch (PatchException) { throw; }
            catch (UnauthorizedAccessException ex)
            {
                throw new PatchException("Access was denied. Close the game and launcher, and check file permissions.", ex);
            }
            catch (IOException ex)
            {
                throw new PatchException("The file operation could not finish. Close the game and launcher, " +
                    "check free disk space and file access, then try again. Any original backup has been retained.", ex);
            }
            catch (ArgumentException ex) { throw new PatchException("The selected file path is invalid.", ex); }
            catch (NotSupportedException ex) { throw new PatchException("This file path or drive is not supported.", ex); }
            catch (System.Security.SecurityException ex)
            {
                throw new PatchException("Windows did not allow access to this file.", ex);
            }
        }
    }
}
