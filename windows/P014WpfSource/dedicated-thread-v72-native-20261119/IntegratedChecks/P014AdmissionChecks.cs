using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CodexAccountSwitcher;
using Microsoft.Win32.SafeHandles;

internal static class P014AdmissionChecks
{
    internal static string[] Cases => [
        "valid", "wrong directory pin", "partial registration", "mixed file generation",
        "changed file bytes", "wrong protected proof", "mixed ledger generation",
        "second Sender", "revoked Sender", "head changed", "account changed"
    ];

    internal static void Run(string @case)
    {
        using var fixture = new Fixture(@case);
        try
        {
            var result = fixture.Run();
            var expected = @case switch {
                "valid" => "SYNTHETIC_HANDLE_PREFLIGHT_PASS_NO_SEND",
                "wrong protected proof" => "HOLD_FIXTURE_DPAPI_BINDING",
                "mixed ledger generation" or "second Sender" or "revoked Sender" => "HOLD_FIXTURE_LEDGER_BINDING",
                "head changed" or "account changed" => "HOLD_FIXTURE_ADMISSION_CHANGED",
                _ => throw new InvalidOperationException("Expected failure before protected proof")
            };
            if (result != expected) throw new InvalidOperationException(result);
            if (fixture.Decodes != 2)
                throw new InvalidOperationException("Expected two synthetic decodes only");
        }
        catch (IOException exception) when (@case is "partial registration" or "wrong directory pin")
        {
            if (@case == "partial registration" && !exception.Message.StartsWith("HOLD_ADMISSION_RELATIVE_OPEN_") ||
                @case == "wrong directory pin" && exception.Message != "HOLD_ADMISSION_ANCHOR_MISMATCH" ||
                fixture.Decodes != 0)
                throw;
        }
        catch (InvalidDataException exception)
        {
            var expected = @case switch {
                "wrong directory pin" => "HOLD_ADMISSION_ANCHOR_MISMATCH",
                "mixed file generation" => "HOLD_FIXTURE_FILE_GENERATION",
                "changed file bytes" => "HOLD_FIXTURE_FILE_HASH",
                _ => ""
            };
            if (exception.Message != expected || fixture.Decodes != 0) throw;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "codex-p014-" + Guid.NewGuid().ToString("N"));
        private readonly string generationPath;
        private readonly string scenario;
        private readonly WindowsAdmissionFixtureFlow.Binding binding;
        private readonly WindowsAdmissionFixtureFlow.Head head;
        private readonly WindowsAdmissionFixtureFlow.Ledger ledger;
        private readonly string key = new('a', 64);
        private int trustReads, accountReads;
        public int Decodes { get; private set; }

        internal Fixture(string scenario)
        {
            this.scenario = scenario;
            generationPath = Directory.CreateDirectory(Path.Combine(folder, "generation-14")).FullName;
            binding = new(14, Guid.NewGuid(), "S-1-5-21-100-200-300-400",
                Guid.NewGuid(), new string('b',64), Guid.NewGuid(), "fixture-owner");
            var startup = Write("codex-owner-startup-v1.json", binding.Generation, new {
                PrivateConfigDpapiBase64 = Convert.ToBase64String(new byte[] { 1, 2 })
            });
            var activation = Write("codex-chat-production-v1.json", binding.Generation, new {
                enabled = true, profileId = binding.ProfileId, accountSha256 = binding.AccountSha256,
                threadId = binding.ThreadId, ownerDevice = binding.DeviceId
            });
            var ipc = Write("codex-due-ipc-enrollment-v1.json", scenario == "mixed file generation" ? 13 : binding.Generation, new {
                ProfileId = binding.ProfileId, AccountSha256 = binding.AccountSha256,
                ThreadId = binding.ThreadId, ProtectedSecretBase64 = Convert.ToBase64String(new byte[] { 3, 4 })
            });
            head = new(42, binding, startup, activation, ipc, false);
            var sender = new WindowsAdmissionFixtureFlow.Sender(binding.DeviceId, true,
                scenario == "revoked Sender", key);
            ledger = new(scenario == "mixed ledger generation" ? binding with { Generation = 13 } : binding,
                scenario == "second Sender" ? [sender, sender with { DeviceId = "another" }] : [sender]);
            if (scenario == "partial registration") File.Delete(Path.Combine(generationPath, "codex-due-ipc-enrollment-v1.json"));
            if (scenario == "changed file bytes") File.AppendAllText(Path.Combine(generationPath, "codex-owner-startup-v1.json"), " ");
        }

        internal string Run()
        {
            using var root = OpenDirectory(folder);
            using var directory = OpenDirectory(generationPath);
            using var central = new SqliteFixture(ledger);
            var rootPin = Anchor(WindowsAdmissionFileEvidence.Inspect(root, directory: true));
            var child = Anchor(WindowsAdmissionFileEvidence.Inspect(directory, directory: true));
            if (scenario == "wrong directory pin") child = child with { OwnerSid = "S-1-0-0" };
            return WindowsAdmissionFixtureFlow.Check(root, rootPin, [("generation-14", child)],
                () => ++trustReads == 2 && scenario == "head changed" ? head with { Revision = 43 } : head,
                () => ++accountReads == 2 && scenario == "account changed" ? binding with { ProfileId = Guid.Empty } : binding,
                approved => approved == binding ? central.Read() : throw new InvalidOperationException(),
                bytes => {
                    Decodes++;
                    if (!bytes.AsSpan().SequenceEqual(new byte[] { 1, 2 })) throw new InvalidOperationException();
                    return new(scenario == "wrong protected proof" ? binding with { Generation = 13 } : binding, key, key);
                },
                bytes => {
                    Decodes++;
                    if (!bytes.AsSpan().SequenceEqual(new byte[] { 3, 4 })) throw new InvalidOperationException();
                    return new(binding, key);
                });
        }

        private WindowsAdmissionFixtureFlow.FilePin Write(string name, long generation, object fields)
        {
            var path = Path.Combine(generationPath, name);
            var values = new Dictionary<string, object?> {
                ["schemaVersion"] = 1, ["generation"] = generation, ["approvalId"] = binding.ApprovalId
            };
            foreach (var field in JsonSerializer.SerializeToElement(fields).EnumerateObject())
                values.Add(field.Name, field.Value);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(values);
            File.WriteAllBytes(path, bytes);
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new(Anchor(WindowsAdmissionFileEvidence.Inspect(file)),
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }

        public void Dispose()
        {
            foreach (var path in Directory.GetFiles(generationPath)) File.Delete(path);
            Directory.Delete(generationPath);
            Directory.Delete(folder);
        }
    }

    private sealed class SqliteFixture : IDisposable
    {
        private readonly IntPtr db;

        internal SqliteFixture(WindowsAdmissionFixtureFlow.Ledger row)
        {
            if (sqlite3_open(":memory:", out db) != 0)
            {
                Dispose();
                throw new InvalidOperationException("SQLite fixture open");
            }
            try
            {
                Exec("CREATE TABLE registration (fixture TEXT NOT NULL)");
                var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(row));
                Exec($"INSERT INTO registration VALUES ('{encoded}')");
                Exec("PRAGMA query_only=ON; BEGIN");
            }
            catch { Dispose(); throw; }
        }

        internal WindowsAdmissionFixtureFlow.Ledger Read()
        {
            string? cell = null;
            int rows = 0;
            Exec("SELECT fixture FROM registration", (_, count, values, _) => {
                if (count != 1 || ++rows != 1) return 1;
                cell = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(values));
                return 0;
            });
            if (cell is null) throw new InvalidOperationException("Missing SQLite fixture row");
            return JsonSerializer.Deserialize<WindowsAdmissionFixtureFlow.Ledger>(
                Convert.FromBase64String(cell)) ?? throw new InvalidOperationException("Missing ledger fixture");
        }

        private void Exec(string sql, RowCallback? callback = null)
        {
            int status = sqlite3_exec(db, sql, callback, IntPtr.Zero, out var error);
            if (error != IntPtr.Zero) sqlite3_free(error);
            if (status != 0) throw new InvalidOperationException("SQLite fixture query: " + status);
        }
        public void Dispose() { if (db != IntPtr.Zero) sqlite3_close(db); }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int RowCallback(IntPtr context, int count, IntPtr values, IntPtr names);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
            RowCallback? callback, IntPtr context, out IntPtr error);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free(IntPtr value);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);
    }
    private static WindowsAdmissionRelativeReader.Anchor Anchor(WindowsAdmissionFileEvidence.Identity id) =>
        new(id.Volume, id.FileId, id.OwnerSid, id.DaclSha256, id.ProtectedDacl);

    private static SafeFileHandle OpenDirectory(string path)
    {
        var handle = CreateFileW(path, 0x120081, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new System.ComponentModel.Win32Exception(error);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
}
