// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RedStarOS.Services;

/// <summary>An app's recent files, for its taskbar button's menu (as Windows' jump lists): the files it opens.</summary>
public static class JumpList
{
    private static readonly Dictionary<string, string> AppOf = new()
    {
        ["Image Viewer"] = "imageviewer", ["Writer"] = "writer", ["CD Player"] = "cdplayer", ["Video"] = "video",
        ["Sheets"] = "sheets", ["Slides"] = "slides", ["Crimson Online"] = "online",
    };

    /// <summary>Whether the app opens this file (Studio opens Writer's, Sheets' and Slides').</summary>
    public static bool Opens(string appId, string path)
    {
        var app = FileOpener.RedStarAppFor(path) is { } name && AppOf.TryGetValue(name, out var id) ? id : null;
        return app == appId || (appId == "studio" && app is "writer" or "sheets" or "slides");
    }

    /// <summary>The newest files of this app, from the given list (newest first), that still exist.</summary>
    public static List<string> Recent(string appId, IEnumerable<string> newestFirst, int max = 7, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        return newestFirst.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal)
            .Where(p => Opens(appId, p) && exists(p)).Take(max).ToList();
    }

    public static List<string> Recent(string appId) =>
        Recent(appId, Places.RecentFiles(80).Select(r => r.Path).Concat(RecentDocs.List(30)));
}
