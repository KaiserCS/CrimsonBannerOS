// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;

namespace RedStarOS.Services.Software;

/// <summary>Where a program in the software centre comes from.</summary>
public enum SoftwareSource { CrimsonBanner, Flatpak, AppImage }

/// <summary>One way to install a program: a pacman package, a Flatpak or an AppImage.</summary>
public sealed record InstallMethod(string Kind)
{
    public const string Pacman = "pacman", Flatpak = "flatpak", AppImage = "appimage";
    public string? Package { get; init; }           // pacman: the package name (in [crimson-banner] or Arch's repositories)
    public string? Remote { get; init; }            // flatpak: the remote's name, e.g. flathub
    public string? RemoteUrl { get; init; }         // flatpak: its .flatpakrepo, added when missing
    public string? Ref { get; init; }               // flatpak: the app id or full ref (app/org.example.App/x86_64/stable)
    public string? Url { get; init; }               // appimage: a direct download
    public string? Sha256 { get; init; }            // appimage: its checksum (required in our catalogue)
    public string? GitHub { get; init; }            // appimage: owner/repo whose latest release has the AppImage
}

/// <summary>A program the software centre can show, install, open, update or remove.</summary>
public sealed class SoftwareItem
{
    public SoftwareSource Source { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Description { get; init; } = "";
    public string? License { get; init; }
    public string? Developer { get; init; }
    public string? Homepage { get; init; }
    public string? Version { get; init; }
    public string? IconPath { get; set; }            // a local picture (AppStream's cached icon, an AppImage's .DirIcon)
    public string? IconUrl { get; init; }            // or one to fetch
    public List<string> Categories { get; init; } = new();
    public List<string> Screenshots { get; init; } = new();   // picture URLs, the main one first
    public List<InstallMethod> Methods { get; init; } = new();

    // what's on this computer
    public bool Installed { get; set; }
    public string? InstalledVersion { get; set; }
    public bool UpdateAvailable { get; set; }
    public string? InstalledWhere { get; set; }      // flatpak: user or system; appimage: the file

    public FreeLicences.Verdict Licence => FreeLicences.Judge(License);
    public override string ToString() => Name;
}
