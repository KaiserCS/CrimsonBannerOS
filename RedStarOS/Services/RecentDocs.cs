using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RedStarOS.Services;

/// <summary>Recently used documents shared by Writer, Sheets, Slides and the Studio launcher.</summary>
public static class RecentDocs
{
    public static readonly string[] Extensions =
        { ".docx", ".txt", ".md", ".html", ".htm", ".xlsx", ".csv", ".tsv", ".rsslides" };

    public static void Add(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var list = SettingsStore.Current.RecentFiles;
            list.RemoveAll(p => string.Equals(p, full, StringComparison.Ordinal));
            list.Insert(0, full);
            if (list.Count > 40) list.RemoveRange(40, list.Count - 40);
            SettingsStore.Save();
        }
        catch (Exception) { /* a recent list is never worth an error */ }
    }

    /// <summary>Existing recent documents, newest first. Includes files Red Star Writer (Electron) opened.</summary>
    public static List<string> List(int max = 12)
    {
        var all = new List<string>(SettingsStore.Current.RecentFiles);
        try
        {
            var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(cfg)) cfg = Path.Combine(AppPaths.Home, ".config");
            var f = Path.Combine(cfg, "redstar-writer", "recent.json");
            if (File.Exists(f))
            {
                var writer = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(f));
                if (writer != null) all.AddRange(writer);
            }
        }
        catch (Exception) { /* ignore */ }

        return all.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p) &&
                              Extensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                  .Distinct()
                  .Take(max)
                  .ToList();
    }

    public static string Describe(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" or ".txt" or ".md" or ".html" or ".htm" => "Document",
            ".xlsx" or ".csv" or ".tsv" => "Spreadsheet",
            ".rsslides" => "Presentation",
            _ => "File"
        };
    }
}
