// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RedStarOS.Services.Software;

/// <summary>
/// Whether a program's licence is a free software licence, from its SPDX expression (AppStream's project_license,
/// AppImageHub's license, our catalogue's licence). The software centre shows only free software (PRINCIPLES.md:
/// "never proprietary"; the FSF's FSDG asks the same of what a distribution steers people to). An expression is
/// free when every licence it requires is: "A AND B" needs both, "A OR B" either one. A licence not on the list is
/// treated as not free, so an unknown one is never presented as free.
/// </summary>
public static class FreeLicences
{
    public enum Verdict { Free, NotFree, Unstated }

    // licences the FSF lists as free software licences (gnu.org/licenses/license-list), by SPDX id; also the
    // public-domain dedications and the documentation/content licences free programs ship their data under
    private static readonly HashSet<string> Free = new(StringComparer.OrdinalIgnoreCase)
    {
        "GPL-1.0", "GPL-2.0", "GPL-3.0", "LGPL-2.0", "LGPL-2.1", "LGPL-3.0", "AGPL-3.0",
        "GPL-1.0+", "GPL-2.0+", "GPL-3.0+", "LGPL-2.0+", "LGPL-2.1+", "LGPL-3.0+", "AGPL-3.0+",
        "GPL-1.0-only", "GPL-1.0-or-later", "GPL-2.0-only", "GPL-2.0-or-later", "GPL-3.0-only", "GPL-3.0-or-later",
        "LGPL-2.0-only", "LGPL-2.0-or-later", "LGPL-2.1-only", "LGPL-2.1-or-later", "LGPL-3.0-only", "LGPL-3.0-or-later",
        "AGPL-3.0-only", "AGPL-3.0-or-later", "GFDL-1.1", "GFDL-1.2", "GFDL-1.3", "GFDL-1.1-or-later", "GFDL-1.2-or-later",
        "GFDL-1.3-or-later", "GFDL-1.1-only", "GFDL-1.2-only", "GFDL-1.3-only",
        "MPL-1.1", "MPL-2.0", "MPL-2.0-no-copyleft-exception", "EPL-1.0", "EPL-2.0", "EUPL-1.1", "EUPL-1.2", "CDDL-1.0",
        "CPL-1.0", "IPL-1.0", "OSL-1.0", "OSL-2.0", "OSL-2.1", "OSL-3.0", "AFL-1.1", "AFL-1.2", "AFL-2.0", "AFL-2.1", "AFL-3.0",
        "Apache-1.0", "Apache-1.1", "Apache-2.0", "Artistic-2.0", "ClArtistic", "MIT", "MIT-0", "X11", "Expat", "ISC", "0BSD",
        "BSD-2-Clause", "BSD-2-Clause-FreeBSD", "BSD-2-Clause-Patent", "BSD-3-Clause", "BSD-3-Clause-Clear", "Zlib",
        "zlib-acknowledgement", "BSL-1.0", "NCSA", "PHP-3.01", "Python-2.0", "PSF-2.0", "Ruby", "Unicode-DFS-2016",
        "Unicode-3.0", "Unlicense", "CC0-1.0", "WTFPL", "Vim", "OpenSSL", "SSLeay-standalone", "IJG", "libpng", "FTL",
        "OFL-1.0", "OFL-1.1", "OFL-1.1-RFN", "OFL-1.1-no-RFN", "CC-BY-3.0", "CC-BY-4.0", "CC-BY-SA-3.0", "CC-BY-SA-4.0",
        "ECL-2.0", "UPL-1.0", "Sleepycat", "Zend-2.0", "W3C", "Xnet", "HPND", "LPPL-1.3a", "LPPL-1.3c", "NTP", "QPL-1.0",
        "SGI-B-2.0", "Intel", "Imlib2", "MirOS", "Fair", "ZPL-2.0", "ZPL-2.1", "LicenseRef-public-domain",
    };

    private static readonly Regex Token = new(@"\(|\)|\bAND\b|\bOR\b|\bWITH\b|[A-Za-z0-9.+\-]+", RegexOptions.IgnoreCase);

    public static Verdict Judge(string? spdx)
    {
        if (string.IsNullOrWhiteSpace(spdx)) return Verdict.Unstated;
        var text = spdx.Trim();
        try
        {
            var tokens = Token.Matches(text).Select(m => m.Value).ToList();
            int i = 0;
            bool result = ParseOr(tokens, ref i);
            return i == tokens.Count && result ? Verdict.Free : Verdict.NotFree;
        }
        catch (FormatException) { return Verdict.NotFree; }
    }

    public static bool IsFree(string? spdx) => Judge(spdx) == Verdict.Free;

    // OR binds looser than AND (SPDX); WITH attaches an exception, which only ever adds permissions
    private static bool ParseOr(List<string> t, ref int i)
    {
        bool v = ParseAnd(t, ref i);
        while (i < t.Count && t[i].Equals("OR", StringComparison.OrdinalIgnoreCase)) { i++; v = ParseAnd(t, ref i) | v; }
        return v;
    }

    private static bool ParseAnd(List<string> t, ref int i)
    {
        bool v = ParseTerm(t, ref i);
        while (i < t.Count && t[i].Equals("AND", StringComparison.OrdinalIgnoreCase)) { i++; v = ParseTerm(t, ref i) & v; }
        return v;
    }

    private static bool ParseTerm(List<string> t, ref int i)
    {
        if (i >= t.Count) throw new FormatException();
        bool v;
        if (t[i] == "(")
        {
            i++;
            v = ParseOr(t, ref i);
            if (i >= t.Count || t[i] != ")") throw new FormatException();
            i++;
        }
        else if (t[i] == ")" || IsOperator(t[i])) throw new FormatException();
        else v = Free.Contains(t[i++]);
        if (i < t.Count && t[i].Equals("WITH", StringComparison.OrdinalIgnoreCase))
        {
            i++;
            if (i >= t.Count || t[i] == "(" || t[i] == ")" || IsOperator(t[i])) throw new FormatException();
            i++;
        }
        return v;
    }

    private static bool IsOperator(string s) =>
        s.Equals("AND", StringComparison.OrdinalIgnoreCase) || s.Equals("OR", StringComparison.OrdinalIgnoreCase) || s.Equals("WITH", StringComparison.OrdinalIgnoreCase);
}
