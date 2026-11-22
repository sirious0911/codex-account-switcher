using System;
using System.Security.AccessControl;

namespace CodexAccountSwitcher;

internal static class WindowsP015AclPolicyChecks
{
    internal static int Run()
    {
        int passed = 0;
        void Check(string name, string sddl, bool registry, bool expected)
        {
            var accepted = WindowsP015AclPolicy.Accepts(
                new RawSecurityDescriptor(sddl), registry);
            if (accepted != expected)
                throw new InvalidOperationException("ACL_POLICY_TEST_FAILED_" + name);
            Console.WriteLine("PASS " + name);
            passed++;
        }

        Check("registry admin/system write and users read",
            "O:BAG:BAD:P(A;;KA;;;BA)(A;;KA;;;SY)(A;;KR;;;BU)", true, true);
        Check("registry non-admin value write rejected",
            "O:BAG:BAD:P(A;;KA;;;BA)(A;;KW;;;BU)", true, false);
        Check("registry non-admin generic all rejected",
            "O:BAG:BAD:P(A;;GA;;;WD)", true, false);
        Check("registry inherited DACL rejected",
            "O:BAG:BAD:(A;;KA;;;BA)", true, false);
        Check("registry user owner rejected",
            "O:BUG:BUD:P(A;;KA;;;BA)", true, false);
        Check("signed HEAD admin/system full and users read allowed",
            "O:SYG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;BU)", false, true);
        Check("signed HEAD non-admin write rejected",
            "O:SYG:SYD:P(A;;FA;;;SY)(A;;FW;;;BU)", false, false);
        Check("signed HEAD everyone delete rejected",
            "O:SYG:SYD:P(A;;FA;;;SY)(A;;SD;;;WD)", false, false);
        Check("signed HEAD user owner rejected",
            "O:BUG:BUD:P(A;;FR;;;WD)", false, false);
        Check("signed HEAD inherited DACL rejected",
            "O:SYG:SYD:(A;;FA;;;SY)", false, false);
        Console.WriteLine($"P015_ACL_SYNTHETIC_PASS tests={passed} operatingTrustWrites=0 actualMessages=0");
        return passed == 10 ? 0 : 1;
    }
}
