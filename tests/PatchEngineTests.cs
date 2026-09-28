using System;
using System.IO;
using System.Security.Cryptography;
using DriftLinkFix;

internal static class PatchEngineTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string DirectoryPath;
        internal readonly string Target;
        internal readonly byte[] Original;
        internal readonly byte[] Patched;
        internal readonly PatchSpec Spec;
        internal readonly PatchEngine Engine;

        internal Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "DriftLinkFixTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            Target = Path.Combine(DirectoryPath, "sdaemon.exe");
            Original = new byte[2048];
            for (int i = 0; i < Original.Length; i++) Original[i] = (byte)(i * 29 % 251);
            byte[] before = new byte[] { 0xbe, 0, 0, 0, 0, 0x90 };
            byte[] after = new byte[] { 0x8b, 0xf1, 0x90, 0x90, 0x90, 0x90 };
            Array.Copy(before, 0, Original, 123, before.Length);
            Patched = (byte[])Original.Clone();
            Array.Copy(after, 0, Patched, 123, after.Length);
            Spec = new PatchSpec(Original.Length, 123, before, after, Hash(Original), Hash(Patched));
            Engine = new PatchEngine(Spec);
            File.WriteAllBytes(Target, Original);
        }

        internal string Backup { get { return Engine.GetBackupPath(Target); } }
        public void Dispose()
        {
            string target = Path.GetFullPath(DirectoryPath);
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!String.Equals(Path.GetDirectoryName(target), tempRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(target).StartsWith("DriftLinkFixTests-", StringComparison.Ordinal) ||
                (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to delete an unexpected test directory.");
            Directory.Delete(target, true);
        }
    }

    private static string Hash(byte[] data)
    {
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Equal(byte[] expected, string path)
    {
        Assert(PatchSpec.Equal(expected, File.ReadAllBytes(path)), "Unexpected content: " + path);
    }

    private static void Refuses(Action action)
    {
        try { action(); }
        catch (PatchException) { return; }
        throw new Exception("Expected a friendly PatchException refusal.");
    }

    private static void Copy(Stream source, Stream destination) { source.CopyTo(destination); }
    private static void Replace(string source, string destination) { File.Replace(source, destination, null); }
    private static void FailReplace(string source, string destination) { throw new IOException("Simulated replacement failure."); }

    private static void RoundTrip()
    {
        using (Fixture f = new Fixture())
        {
            Assert(f.Engine.Inspect(f.Target) == PatchState.Original, "Inspect original");
            Assert(!f.Engine.CanRestore(f.Target), "Original cannot restore");
            Assert(f.Backup == f.Target + ".fnf-drift-link-fix.original.bak", "Backup name must match Python");
            Assert(f.Engine.Apply(f.Target) == PatchState.Patched, "Apply result");
            Equal(f.Patched, f.Target);
            Equal(f.Original, f.Backup);
            byte[] actual = File.ReadAllBytes(f.Target);
            for (int i = 0; i < actual.Length; i++)
                if (i < 123 || i >= 129) Assert(actual[i] == f.Original[i], "Changed bytes outside patch region");
            Assert(f.Engine.Inspect(f.Target) == PatchState.Patched, "Inspect patched");
            Assert(f.Engine.CanRestore(f.Target), "Verified backup can restore");
            Assert(f.Engine.Restore(f.Target) == PatchState.Original, "Restore result");
            Equal(f.Original, f.Target);
            Equal(f.Original, f.Backup);
        }
    }

    private static void Idempotent()
    {
        using (Fixture f = new Fixture())
        {
            f.Engine.Apply(f.Target);
            DateTime sentinel = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(f.Target, sentinel);
            File.SetLastWriteTimeUtc(f.Backup, sentinel);
            PatchEngine noReplace = new PatchEngine(f.Spec, Copy, FailReplace);
            Assert(noReplace.Apply(f.Target) == PatchState.Patched, "Already patched returns patched");
            Assert(File.GetLastWriteTimeUtc(f.Target) == sentinel, "Idempotent apply rewrote target");
            Assert(File.GetLastWriteTimeUtc(f.Backup) == sentinel, "Idempotent apply rewrote backup");
            File.Delete(f.Backup);
            Assert(noReplace.Apply(f.Target) == PatchState.Patched, "Patched file needs no backup for no-op apply");
            Assert(!f.Engine.CanRestore(f.Target), "No backup cannot restore");
        }
    }

    private static void UnknownHash()
    {
        using (Fixture f = new Fixture())
        {
            byte[] unknown = (byte[])f.Original.Clone();
            unknown[0] ^= 0xff;
            File.WriteAllBytes(f.Target, unknown);
            Refuses(delegate { f.Engine.Inspect(f.Target); });
            Refuses(delegate { f.Engine.Apply(f.Target); });
            Refuses(delegate { f.Engine.Restore(f.Target); });
            Equal(unknown, f.Target);
            Assert(!File.Exists(f.Backup), "Unknown file must not create backup");
        }
    }

    private static void WrongSize()
    {
        using (Fixture f = new Fixture())
        {
            byte[] wrong = new byte[17];
            File.WriteAllBytes(f.Target, wrong);
            Refuses(delegate { f.Engine.Apply(f.Target); });
            Equal(wrong, f.Target);
            Assert(!File.Exists(f.Backup), "Wrong size created a backup");
        }
    }

    private static void InvalidBackupPreserved()
    {
        using (Fixture f = new Fixture())
        {
            byte[] important = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(f.Backup, important);
            Refuses(delegate { f.Engine.Apply(f.Target); });
            Equal(important, f.Backup);
            Equal(f.Original, f.Target);
            File.WriteAllBytes(f.Target, f.Patched);
            Assert(!f.Engine.CanRestore(f.Target), "Invalid backup cannot restore");
            Refuses(delegate { f.Engine.Restore(f.Target); });
            Equal(f.Patched, f.Target);
        }
    }

    private static void ExistingOriginalBackupReused()
    {
        using (Fixture f = new Fixture())
        {
            File.WriteAllBytes(f.Backup, f.Original);
            DateTime sentinel = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(f.Backup, sentinel);
            f.Engine.Apply(f.Target);
            Assert(File.GetLastWriteTimeUtc(f.Backup) == sentinel, "Valid existing backup was overwritten");
            Equal(f.Original, f.Backup);
        }
    }

    private static void RestoreRequiresBothFiles()
    {
        using (Fixture f = new Fixture())
        {
            Refuses(delegate { f.Engine.Restore(f.Target); });
            File.WriteAllBytes(f.Target, f.Patched);
            Refuses(delegate { f.Engine.Restore(f.Target); });
            File.WriteAllBytes(f.Backup, f.Patched);
            Assert(!f.Engine.CanRestore(f.Target), "Patched backup is not original");
            Refuses(delegate { f.Engine.Restore(f.Target); });
            Equal(f.Patched, f.Target);
        }
    }

    private static void InUseFileRefused()
    {
        using (Fixture f = new Fixture())
        {
            using (FileStream held = new FileStream(f.Target, FileMode.Open, FileAccess.Read, FileShare.Read))
                Refuses(delegate { f.Engine.Apply(f.Target); });
            Equal(f.Original, f.Target);
            Assert(!File.Exists(f.Backup), "In-use target created a backup");
        }
    }

    private static void FailedReplaceRecoverable()
    {
        using (Fixture f = new Fixture())
        {
            PatchEngine failing = new PatchEngine(f.Spec, Copy, FailReplace);
            Refuses(delegate { failing.Apply(f.Target); });
            Equal(f.Original, f.Target);
            Equal(f.Original, f.Backup);
            Assert(Directory.GetFiles(f.DirectoryPath, "*.tmp").Length == 0, "Leaked staged file");
            f.Engine.Apply(f.Target);
            Refuses(delegate { failing.Restore(f.Target); });
            Equal(f.Patched, f.Target);
            Equal(f.Original, f.Backup);
            f.Engine.Restore(f.Target);
            Equal(f.Original, f.Target);
        }
    }

    private static void InterruptedBackupWrite()
    {
        using (Fixture f = new Fixture())
        {
            Action<Stream, Stream> interrupted = delegate(Stream source, Stream destination)
            {
                destination.WriteByte((byte)source.ReadByte());
                throw new IOException("Simulated full disk during backup.");
            };
            PatchEngine failing = new PatchEngine(f.Spec, interrupted, Replace);
            Refuses(delegate { failing.Apply(f.Target); });
            Equal(f.Original, f.Target);
            Assert(File.ReadAllBytes(f.Backup).Length == 1, "Expected partial backup for no-overwrite test");
            Refuses(delegate { f.Engine.Apply(f.Target); });
            Assert(File.ReadAllBytes(f.Backup).Length == 1, "Partial backup was silently overwritten");
        }
    }

    private static void InterruptedStagingWrite()
    {
        using (Fixture f = new Fixture())
        {
            int copies = 0;
            Action<Stream, Stream> interrupted = delegate(Stream source, Stream destination)
            {
                copies++;
                if (copies == 1) { source.CopyTo(destination); return; }
                destination.WriteByte((byte)source.ReadByte());
                throw new IOException("Simulated full disk while staging.");
            };
            PatchEngine failing = new PatchEngine(f.Spec, interrupted, Replace);
            Refuses(delegate { failing.Apply(f.Target); });
            Equal(f.Original, f.Target);
            Equal(f.Original, f.Backup);
            Assert(Directory.GetFiles(f.DirectoryPath, "*.tmp").Length == 0, "Leaked partial staged file");
            f.Engine.Apply(f.Target);
        }
    }

    private static void CorruptedStagingRejected()
    {
        using (Fixture f = new Fixture())
        {
            File.WriteAllBytes(f.Backup, f.Original);
            Action<Stream, Stream> corrupt = delegate(Stream source, Stream destination)
            {
                source.CopyTo(destination);
                destination.Position = 0;
                destination.WriteByte(255);
            };
            PatchEngine failing = new PatchEngine(f.Spec, corrupt, Replace);
            Refuses(delegate { failing.Apply(f.Target); });
            Equal(f.Original, f.Target);
            Equal(f.Original, f.Backup);
            Assert(Directory.GetFiles(f.DirectoryPath, "*.tmp").Length == 0, "Leaked corrupt staged file");
        }
    }

    private static void BadPathsAreFriendly()
    {
        PatchEngine engine = new PatchEngine();
        Refuses(delegate { engine.Inspect(null); });
        Refuses(delegate { engine.Apply(" "); });
        Refuses(delegate { engine.Inspect("bad\0path"); });
        Assert(!engine.CanRestore("bad\0path"), "Invalid path cannot restore");
    }

    public static int Main()
    {
        Action[] tests = new Action[] {
            RoundTrip, Idempotent, UnknownHash, WrongSize, InvalidBackupPreserved,
            ExistingOriginalBackupReused, RestoreRequiresBothFiles, InUseFileRefused,
            FailedReplaceRecoverable, InterruptedBackupWrite, InterruptedStagingWrite,
            CorruptedStagingRejected, BadPathsAreFriendly
        };
        int failures = 0;
        foreach (Action test in tests)
        {
            try { test(); Console.WriteLine("PASS " + test.Method.Name); }
            catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + test.Method.Name + ": " + ex); }
        }
        Console.WriteLine((tests.Length - failures) + "/" + tests.Length + " tests passed.");
        return failures == 0 ? 0 : 1;
    }
}
