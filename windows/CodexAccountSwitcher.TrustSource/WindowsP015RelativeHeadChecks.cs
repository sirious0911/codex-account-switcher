using System;
using System.IO;

namespace CodexAccountSwitcher;

internal static class WindowsP015RelativeHeadChecks
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "p015-relative-head-isolated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0;
        if (Directory.Exists(Path.Combine(root, "CodexAccountSwitcher")))
            throw new InvalidDataException("FIXTURE_CHILD_PREEXISTED");
        try
        {
            try
            {
                WindowsP015RelativeHeadReader.ReadFromFixtureRoot(root);
                throw new InvalidDataException("UNEXPECTED_MISSING_DIRECTORY_ACCEPTED");
            }
            catch (InvalidDataException e) when (
                e.Message.StartsWith("HOLD_P015_RELATIVE_OPEN_", StringComparison.Ordinal))
            {
                if (Directory.Exists(Path.Combine(root, "CodexAccountSwitcher")) ||
                    Directory.GetFileSystemEntries(root).Length != 0)
                    throw new InvalidDataException("READONLY_RELATIVE_OPEN_CREATED_CHILD");
                Console.WriteLine("PASS native relative open rejects missing child without creation");
                ++passed;
            }
            catch (InvalidDataException e)
            {
                Console.WriteLine("UNEXPECTED_NATIVE_HOLD=" + e.Message);
                Console.WriteLine("CHILD_EXISTS_AFTER_CALL=" +
                    Directory.Exists(Path.Combine(root, "CodexAccountSwitcher")));
                throw;
            }
            var parent = Directory.CreateDirectory(Path.Combine(root,
                "CodexAccountSwitcher")).FullName;
            Directory.CreateDirectory(Path.Combine(parent, "OwnerTrustV1"));
            File.WriteAllText(Path.Combine(parent, "OwnerTrustV1",
                "head-envelope.json"), "{\"schemaVersion\":1}");
            try
            {
                WindowsP015RelativeHeadReader.ReadFromFixtureRoot(root);
                throw new InvalidDataException("UNEXPECTED_USER_WRITABLE_ROOT_ACCEPTED");
            }
            catch (InvalidDataException e) when (
                e.Message == "HOLD_P015_PARENT_ACL")
            {
                Console.WriteLine("PASS native relative open denies user-owned application directory");
                ++passed;
            }
            Console.WriteLine($"P015_RELATIVE_HEAD_SYNTHETIC_PASS tests={passed} operatingTrustWrites=0 actualMessages=0");
            return passed == 2 ? 0 : 1;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
