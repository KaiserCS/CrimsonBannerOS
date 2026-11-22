// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using RedStarOS.Services;
using RedStarOS.Views;
using Xunit;

namespace RedStarOS.Tests;

public class MusicLibraryTests
{
    [Theory]
    [InlineData("/m/Music/Kraftwerk/Radio-Activity/03 - Airwaves.flac", 3, "Airwaves", "Kraftwerk", "Radio-Activity")]
    [InlineData("/m/Music/Kraftwerk - Computer World/01. Pocket Calculator.mp3", 1, "Pocket Calculator", "Kraftwerk", "Computer World")]
    [InlineData("/m/Music/Mix/07 Laibach - Life Is Life.ogg", 7, "Life Is Life", "Laibach", "Mix")]
    [InlineData("/m/Music/song_title.opus", 0, "song title", MusicLibrary.UnknownArtist, "Loose tracks")]
    [InlineData("/m/Music/Artist/Album/CD1-02 Second.flac", 2, "Second", "Artist", "Album")]
    public void A_track_is_read_from_its_path(string path, int number, string title, string artist, string album)
    {
        var t = MusicLibrary.FromPath(path);
        Assert.Equal(number, t.Number);
        Assert.Equal(title, t.Title);
        Assert.Equal(artist, t.Artist);
        Assert.Equal(album, t.Album);
    }

    [Fact]
    public void Albums_are_folders_in_track_order()
    {
        var files = new[]
        {
            "/m/B/Second/02 b.mp3", "/m/B/Second/01 a.mp3", "/m/B/Second/notes.txt",
            "/m/A/First/10 ten.flac", "/m/A/First/2 two.flac", "/m/A/First/bonus.flac"
        };
        var albums = MusicLibrary.Group(files, _ => null);
        Assert.Equal(new[] { "First", "Second" }, albums.Select(a => a.Title));
        Assert.Equal(new[] { "two", "ten", "bonus" }, albums[0].Tracks.Select(t => t.Title));
        Assert.Equal("A", albums[0].Artist);
        Assert.Equal(2, albums[1].Tracks.Count);
    }

    [Fact]
    public void The_cover_is_found_next_to_the_tracks()
    {
        var dir = Directory.CreateTempSubdirectory("cdp-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "01 a.mp3"), "");
            File.WriteAllText(Path.Combine(dir, "back.jpg"), "");
            Assert.Null(MusicLibrary.FindCover(Path.Combine(dir, "missing")));
            Assert.Equal(Path.Combine(dir, "back.jpg"), MusicLibrary.FindCover(dir));      // the only picture
            File.WriteAllText(Path.Combine(dir, "Folder.PNG"), "");
            Assert.Equal(Path.Combine(dir, "Folder.PNG"), MusicLibrary.FindCover(dir));
            File.WriteAllText(Path.Combine(dir, "cover.jpg"), "");
            Assert.Equal(Path.Combine(dir, "cover.jpg"), MusicLibrary.FindCover(dir));

            var albums = MusicLibrary.Scan(dir);
            Assert.Single(albums);
            Assert.Equal(Path.Combine(dir, "cover.jpg"), albums[0].Cover);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_music_folder_comes_from_user_dirs()
    {
        var lines = new[] { "# comment", "XDG_DESKTOP_DIR=\"$HOME/Desktop\"", "XDG_MUSIC_DIR=\"$HOME/Musik\"" };
        Assert.Equal("/home/u/Musik", MusicLibrary.UserDir(lines, "XDG_MUSIC_DIR", "/home/u"));
        Assert.Null(MusicLibrary.UserDir(lines, "XDG_VIDEOS_DIR", "/home/u"));
    }

    [Fact]
    public void The_shelf_filter_matches_every_word()
    {
        var albums = MusicLibrary.Group(new[] { "/m/Kraftwerk/Radio-Activity/01 Geiger Counter.mp3", "/m/Laibach/Opus Dei/01 Leben heisst Leben.mp3" }, _ => null);
        Assert.Single(MusicLibrary.Filter(albums, "kraft radio"));
        Assert.Single(MusicLibrary.Filter(albums, "leben"));
        Assert.Empty(MusicLibrary.Filter(albums, "kraft leben"));
        Assert.Equal(2, MusicLibrary.Filter(albums, "  ").Count());
    }

    [Fact]
    public void Music_opens_in_CD_Player_and_films_in_Video()
    {
        Assert.Equal("CD Player", FileOpener.RedStarAppFor("/x/a.flac"));
        Assert.Equal("Video", FileOpener.RedStarAppFor("/x/a.mkv"));
        Assert.NotNull(AppRegistry.Find("cdplayer"));
    }
}

public class CdPlayerWindowTests
{
    [AvaloniaFact]
    public void Plays_through_its_panel_without_libvlc()
    {
        // without libVLC the window still works as a panel (it says what to install), so every part of it can be
        // driven here
        var w = new CdPlayerWindow(null);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var album = MusicLibrary.Group(new[] { "/m/Artist/Album/01 One.flac", "/m/Artist/Album/02 Two.flac", "/m/Artist/Album/03 Three.flac" }, _ => null)[0];
        w.LoadAlbum(album, "YOUR MUSIC LIBRARY", play: true, first: 1);
        foreach (var k in new[] { Key.Space, Key.L, Key.J, Key.X, Key.R, Key.R, Key.R, Key.U, Key.C, Key.C, Key.S, Key.Escape, Key.E, Key.E, Key.D, Key.Up, Key.Down })
        {
            w.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = k, Source = w });
            Dispatcher.UIThread.RunJobs();
        }
        w.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
