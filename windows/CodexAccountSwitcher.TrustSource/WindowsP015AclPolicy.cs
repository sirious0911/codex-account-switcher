using System;
using System.Security.AccessControl;

namespace CodexAccountSwitcher;

/// <summary>
/// Pure ACL review used on the administrator registry key and signed HEAD file.
/// No filesystem or registry mutations; no account impersonation.
/// </summary>
internal static class WindowsP015AclPolicy
{
    internal const string AdminSid = "S-1-5-32-544";
    internal const string SystemSid = "S-1-5-18";

    // Registry: KEY_SET_VALUE, KEY_CREATE_SUB_KEY, DELETE, WRITE_DAC,
    // WRITE_OWNER, GENERIC_WRITE, GENERIC_ALL.
    private const int RegistryMutation = 0x0002 | 0x0004 |
        0x00010000 | 0x00040000 | 0x00080000 |
        0x40000000 | 0x10000000;

    // File: WRITE_DATA, APPEND_DATA, WRITE_EA, WRITE_ATTRIBUTES,
    // DELETE_CHILD, DELETE, WRITE_DAC, WRITE_OWNER,
    // GENERIC_WRITE, GENERIC_ALL.
    private const int FileMutation = 0x0002 | 0x0004 | 0x0010 |
        0x0040 | 0x0100 | 0x00010000 | 0x00040000 |
        0x00080000 | 0x40000000 | 0x10000000;

    internal static bool Accepts(RawSecurityDescriptor descriptor, bool registry)
    {
        if (descriptor.Owner?.Value is not (AdminSid or SystemSid) ||
            !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected) ||
            descriptor.DiscretionaryAcl is null)
            return false;

        int unsafeMask = registry ? RegistryMutation : FileMutation;
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if (ace is not QualifiedAce q ||
                q.AceQualifier != AceQualifier.AccessAllowed)
                continue;
            var trustee = q.SecurityIdentifier?.Value;
            if (trustee is null ||
                (trustee is not (AdminSid or SystemSid) &&
                 (q.AccessMask & unsafeMask) != 0))
                return false;
        }
        return true;
    }
}
