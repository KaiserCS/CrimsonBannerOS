# People's Archive

Crimson Banner OS's library and reading room, in Software's Crimson Banner tab (`crimson-archive`). It finds free books
in public-domain archives, keeps them on a shelf on your computer, and sets them for reading.

## Sources

| Source | How People's Archive uses it | Run by |
|---|---|---|
| **Project Gutenberg** | Its OPDS catalogue for reading apps (`gutenberg.org/ebooks/search.opds/`), browsed or searched. A book's files come from the feed of the book, never by crawling the website, which Gutenberg keeps for people. | Project Gutenberg Literary Archive Foundation (non-profit) |
| **Wikisource** | Searched through Wikisource's own API, in a dozen languages. A whole work is downloaded as EPUB 3 through WS Export, Wikimedia's tool that assembles a work from its pages. | Wikimedia Foundation (non-profit) |
| **Marxists Internet Archive** | Its ebook collection's index page (`marxists.org/ebooks/`), fetched when asked, kept for a week, and searched on your computer. | volunteers |
| **Any OPDS catalogue** | Sources → Add. Libraries and publishers offer these for reading apps. A sign-in is a user name or email (Standard Ebooks' full catalogue is for its patrons). | whoever runs it |

People's Archive goes online only when you search or download. Every request names it (`PeoplesArchive/1.0`), and
books come one at a time. A download has to be an EPUB or plain text; a web page in its place, as a busy source may
send, is refused with a message. The terms each book comes under go on the shelf with it.

WS Export's download address isn't documented in a form People's Archive could check while it was written. If
Wikisource downloads stop working, `WikisourceSource.ExportUrl` in `src/Model/Sources.cs` is the one line to change.
Project Gutenberg plans to retire its XML OPDS feeds in 2027, for a JSON one (OPDS 2); People's Archive will need to
follow by then.

## The shelf and the reading room

- Books are kept in `~/Documents/People's Archive`, with `library.json` beside them. Any EPUB or `.txt` file dropped
  in the folder, on the window, or opened with the app joins the shelf.
- The reading room reads EPUB 2 and 3 (spine, contents, pictures, cover) and plain text, including Project
  Gutenberg's headers. A chapter becomes headings, paragraphs, quotes, lists and pictures, set in a book face (Noto
  Serif); the book's own styles are left out.
- Paper, sepia or night, type from 12 to 34 points (A− A+, or Ctrl and + −), ← and → between chapters, and a bookmark.
  The place reached is remembered.
- Its settings are in `~/.config/peoples-archive/`; the index it keeps is in `~/.cache/peoples-archive/`.

## Building

The source is in `src/` (C#, Avalonia), sharing the shell's `Styles/Shell.axaml` and `apps/shared`. The engine and
windows are tested with the shell's tests (`tests/RedStarOS.Tests/ArchiveTests.cs`, and screenshots of every page),
on sample books made by `Samples` (the openings of three public-domain books), so the tests need no network.

```bash
dotnet run --project apps/archive/src          # from the tree
tools/publish-app.sh apps/archive              # build, sign and publish it to Software
```
