using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;

namespace RedStarOS.Services;

/// <summary>
/// Red Star OS line icons: drawn as vectors on a 24×24 grid, so they look the same on every distro (emoji fonts
/// differ) and stay sharp at any size. Icons follow the colour they are given (text colour by default).
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["clipboard"] = "M 9,3.5 L 15,3.5 L 15,6.5 L 9,6.5 Z M 7,5 L 5.5,5 L 5.5,21 L 18.5,21 L 18.5,5 L 17,5 M 8.5,11.5 L 15.5,11.5 M 8.5,15.5 L 13,15.5",
        ["blocks"] = "M 4,14 L 10,14 L 10,20 L 4,20 Z M 10,14 L 16,14 L 16,20 L 10,20 Z M 10,8 L 16,8 L 16,14 L 10,14 Z M 16,14 L 22,14 L 22,20 L 16,20 Z M 10,2 L 16,2 L 16,8",
        ["tiles"] = "M 4,5 L 13,5 L 13,18 L 4,18 Z M 13,8 L 20,8 L 20,21 L 9,21 L 9,18 M 6.5,9 L 10.5,9 M 8.5,9 L 8.5,14.5",
        ["gem"] = "M 6,4 L 18,4 L 22,9 L 12,21 L 2,9 Z M 2,9 L 22,9 M 9,4 L 7.5,9 L 12,21 M 15,4 L 16.5,9 L 12,21",
        ["screenshot"] = "M 3,8 L 3,3 L 8,3 M 16,3 L 21,3 L 21,8 M 21,16 L 21,21 L 16,21 M 8,21 L 3,21 L 3,16 M 8.5,8.5 L 15.5,8.5 L 15.5,15.5 L 8.5,15.5 Z",
        ["mic"] = "M 9,3 L 15,3 L 15,13 L 9,13 Z M 5,11 C 5,16 8,18 12,18 C 16,18 19,16 19,11 M 12,18 L 12,21 M 8,21 L 16,21",
        ["document"] = "M 6,3 L 14,3 L 19,8 L 19,21 L 6,21 Z M 14,3 L 14,8 L 19,8 M 9,13 L 16,13 M 9,17 L 14,17",
        ["sheet"] = "M 4,4 L 20,4 L 20,20 L 4,20 Z M 4,9.5 L 20,9.5 M 4,15 L 20,15 M 10,4 L 10,20",
        ["slides"] = "M 3,4.5 L 21,4.5 L 21,16 L 3,16 Z M 12,16 L 12,20 M 8,20.5 L 16,20.5 M 7,12 L 10,9 L 13,11 L 17,7.5",
        ["film"] = "M 3,5 L 21,5 L 21,19 L 3,19 Z M 7,5 L 7,19 M 17,5 L 17,19 M 3,9.5 L 7,9.5 M 3,14.5 L 7,14.5 M 17,9.5 L 21,9.5 M 17,14.5 L 21,14.5",
        ["game"] = "M 5,6 L 5,12 M 19,12 L 19,18 M 12,3.5 L 12,5.5 M 12,8.5 L 12,10.5 M 12,13.5 L 12,15.5 M 12,18.5 L 12,20.5 M 14.5,8 L 16.5,8 L 16.5,10 L 14.5,10 Z",
        ["globe"] = "M 12,3 A 9,9 0 1 1 11.99,3 Z M 3,12 L 21,12 M 12,3 C 8.4,6.6 8.4,17.4 12,21 M 12,3 C 15.6,6.6 15.6,17.4 12,21",
        ["chat"] = "M 4,5 L 20,5 L 20,16 L 10,16 L 6,20 L 6,16 L 4,16 Z M 8,9.5 L 16,9.5 M 8,12.5 L 13,12.5",
        ["folder"] = "M 3,6 L 9,6 L 11,8 L 21,8 L 21,19 L 3,19 Z",
        ["folder-plus"] = "M 3,6 L 9,6 L 11,8 L 21,8 L 21,19 L 3,19 Z M 12,11 L 12,16 M 9.5,13.5 L 14.5,13.5",
        ["file"] = "M 6,3 L 14,3 L 19,8 L 19,21 L 6,21 Z M 14,3 L 14,8 L 19,8",
        ["file-plus"] = "M 6,3 L 14,3 L 19,8 L 19,21 L 6,21 Z M 14,3 L 14,8 L 19,8 M 12.5,11.5 L 12.5,17.5 M 9.5,14.5 L 15.5,14.5",
        ["archive"] = "M 4,4 L 20,4 L 20,8 L 4,8 Z M 5,8 L 5,20 L 19,20 L 19,8 M 10,12 L 14,12",
        ["code"] = "M 8.5,7 L 3.5,12 L 8.5,17 M 15.5,7 L 20.5,12 L 15.5,17 M 13.5,5 L 10.5,19",
        ["music"] = "M 9,18 L 9,5.5 L 19,3.5 L 19,16 M 6.5,15.5 A 2.5,2.5 0 1 1 6.49,15.5 Z M 16.5,13.5 A 2.5,2.5 0 1 1 16.49,13.5 Z",
        ["trash"] = "M 4,6.5 L 20,6.5 M 9,6.5 L 9,4 L 15,4 L 15,6.5 M 6,6.5 L 7,20.5 L 17,20.5 L 18,6.5 M 10,10.5 L 10,16.5 M 14,10.5 L 14,16.5",
        ["drive"] = "M 3,13 L 6,5 L 18,5 L 21,13 L 21,19 L 3,19 Z M 3,13 L 21,13 M 16.5,16 L 16.51,16 M 13.5,16 L 13.51,16",
        ["arrow-left"] = "M 19,12 L 5,12 M 11,6 L 5,12 L 11,18",
        ["arrow-right"] = "M 5,12 L 19,12 M 13,6 L 19,12 L 13,18",
        ["arrow-up"] = "M 12,19 L 12,5 M 6,11 L 12,5 L 18,11",
        ["chevron-right"] = "M 9,6 L 15,12 L 9,18",
        ["chevron-down"] = "M 6,9 L 12,15 L 18,9",
        ["chevron-up"] = "M 6,15 L 12,9 L 18,15",
        ["grid"] = "M 4,4 L 10,4 L 10,10 L 4,10 Z M 14,4 L 20,4 L 20,10 L 14,10 Z M 4,14 L 10,14 L 10,20 L 4,20 Z M 14,14 L 20,14 L 20,20 L 14,20 Z",
        ["list"] = "M 8.5,6 L 20,6 M 8.5,12 L 20,12 M 8.5,18 L 20,18 M 4,6 L 4.01,6 M 4,12 L 4.01,12 M 4,18 L 4.01,18",
        ["eye"] = "M 2.5,12 C 5,6.5 19,6.5 21.5,12 C 19,17.5 5,17.5 2.5,12 Z M 12,9.5 A 2.5,2.5 0 1 1 11.99,9.5 Z",
        ["pin"] = "M 9,4 L 15,4 M 10,4 L 10,10 L 7,13.5 L 17,13.5 L 14,10 L 14,4 M 12,13.5 L 12,20.5",
        ["copy"] = "M 9,9 L 20,9 L 20,20 L 9,20 Z M 6,15 L 4,15 L 4,4 L 15,4 L 15,6",
        ["cut"] = "M 6,6.5 A 2.5,2.5 0 1 1 5.99,6.5 Z M 6,17.5 A 2.5,2.5 0 1 1 5.99,17.5 Z M 8,8 L 20,18 M 8,16 L 20,6",
        ["paste"] = "M 9,3.5 L 15,3.5 L 15,6.5 L 9,6.5 Z M 7,5 L 5.5,5 L 5.5,21 L 18.5,21 L 18.5,5 L 17,5",
        ["edit"] = "M 4,20 L 4,16 L 15,5 L 19,9 L 8,20 Z M 13,7 L 17,11",
        ["restore"] = "M 4,12 A 8,8 0 1 0 6.3,6.3 M 4,4 L 4,9 L 9,9",
        ["sort"] = "M 7,4 L 7,20 M 3.5,16.5 L 7,20 L 10.5,16.5 M 17,20 L 17,4 M 13.5,7.5 L 17,4 L 20.5,7.5",
        ["calculator"] = "M 6,3 L 18,3 L 18,21 L 6,21 Z M 9,6.5 L 15,6.5 M 9,11 L 9.01,11 M 12,11 L 12.01,11 M 15,11 L 15.01,11 M 9,14.5 L 9.01,14.5 M 12,14.5 L 12.01,14.5 M 15,14.5 L 15,18 M 9,18 L 12.01,18",
        ["note"] = "M 5,4 L 19,4 L 19,15 L 14,20 L 5,20 Z M 14,20 L 14,15 L 19,15 M 8,9 L 16,9 M 8,12.5 L 13,12.5",
        ["image"] = "M 3,5 L 21,5 L 21,19 L 3,19 Z M 3,16 L 8,11 L 12,15 L 15,12 L 21,18 M 15.5,9 L 15.51,9",
        ["camera"] = "M 3,8 L 7,8 L 9,5 L 15,5 L 17,8 L 21,8 L 21,19 L 3,19 Z M 12,9.5 A 3.5,3.5 0 1 1 11.99,9.5 Z",
        ["calendar"] = "M 4,6 L 20,6 L 20,20 L 4,20 Z M 4,10 L 20,10 M 8,3.5 L 8,7.5 M 16,3.5 L 16,7.5 M 8,14 L 8.01,14 M 12,14 L 12.01,14 M 16,14 L 16.01,14 M 8,17 L 8.01,17 M 12,17 L 12.01,17",
        ["settings"] = "M 12,8.5 A 3.5,3.5 0 1 1 11.99,8.5 Z M 12,2.5 L 12,5 M 12,19 L 12,21.5 M 2.5,12 L 5,12 M 19,12 L 21.5,12 M 5.3,5.3 L 7.1,7.1 M 16.9,16.9 L 18.7,18.7 M 5.3,18.7 L 7.1,16.9 M 16.9,7.1 L 18.7,5.3",
        ["terminal"] = "M 3,5 L 21,5 L 21,19 L 3,19 Z M 7,10 L 10,12.5 L 7,15 M 12,15 L 17,15",
        ["palette"] = "M 12,3 A 9,9 0 1 0 12,21 C 13.5,21 14,20 13.5,18.5 C 13,17 14,16 15.5,16 L 17.5,16 C 19.5,16 21,14.5 21,12.5 C 21,7.3 17,3 12,3 Z M 7.5,11 L 7.51,11 M 10,7.5 L 10.01,7.5 M 14.5,7.5 L 14.51,7.5 M 17,10.5 L 17.01,10.5",
        ["clock"] = "M 12,3 A 9,9 0 1 1 11.99,3 Z M 12,7 L 12,12 L 15.5,14",
        ["activity"] = "M 3,12 L 7,12 L 10,5 L 14,19 L 17,12 L 21,12",
        ["bell"] = "M 6,16 L 6,11 C 6,7.5 8.5,5 12,5 C 15.5,5 18,7.5 18,11 L 18,16 L 20,18 L 4,18 Z M 10,21 L 14,21 M 12,3 L 12,5",
        ["bell-off"] = "M 6,16 L 6,11 C 6,7.5 8.5,5 12,5 C 15.5,5 18,7.5 18,11 L 18,16 L 20,18 L 4,18 Z M 10,21 L 14,21 M 3,3 L 21,21",
        ["wifi"] = "M 2.5,9 C 8,4 16,4 21.5,9 M 5.5,12.5 C 9.5,9 14.5,9 18.5,12.5 M 8.5,16 C 10.5,14.3 13.5,14.3 15.5,16 M 12,19.5 L 12.01,19.5",
        ["wired"] = "M 9,3 L 9,7 M 15,3 L 15,7 M 6,7 L 18,7 L 18,11 C 18,14 15.5,16 12,16 C 8.5,16 6,14 6,11 Z M 12,16 L 12,21",
        ["offline"] = "M 12,3 A 9,9 0 1 1 11.99,3 Z M 5.6,5.6 L 18.4,18.4",
        ["volume-mute"] = "M 4,9 L 8,9 L 13,5 L 13,19 L 8,15 L 4,15 Z M 16,9.5 L 21,14.5 M 21,9.5 L 16,14.5",
        ["volume-low"] = "M 4,9 L 8,9 L 13,5 L 13,19 L 8,15 L 4,15 Z M 16,10 C 17.2,11.2 17.2,12.8 16,14",
        ["volume-mid"] = "M 4,9 L 8,9 L 13,5 L 13,19 L 8,15 L 4,15 Z M 16,10 C 17.2,11.2 17.2,12.8 16,14 M 18.5,8 C 20.6,10.2 20.6,13.8 18.5,16",
        ["volume"] = "M 4,9 L 8,9 L 13,5 L 13,19 L 8,15 L 4,15 Z M 16,10 C 17.2,11.2 17.2,12.8 16,14 M 18.5,7.5 C 21,10 21,14 18.5,16.5 M 20.5,5 C 24,8.5 24,15.5 20.5,19",
        ["monitor"] = "M 3,4 L 21,4 L 21,16 L 3,16 Z M 12,16 L 12,20 M 8,20 L 16,20",
        ["user"] = "M 12,4 A 4,4 0 1 1 11.99,4 Z M 4,21 C 4,16.5 7.5,14 12,14 C 16.5,14 20,16.5 20,21",
        ["refresh"] = "M 20,4 L 20,9.5 L 14.5,9.5 M 19.6,15 A 8,8 0 1 1 18.4,7.9 L 20,9.5",
        ["record"] = "M 12,6 A 6,6 0 1 1 11.99,6 Z",
        ["play"] = "M 7,4 L 19,12 L 7,20 Z",
        ["pause"] = "M 8,5 L 8,19 M 16,5 L 16,19",
        ["star"] = "M 12,3 L 14.8,8.8 L 21,9.6 L 16.5,14 L 17.6,20.3 L 12,17.3 L 6.4,20.3 L 7.5,14 L 3,9.6 L 9.2,8.8 Z",
        ["plus"] = "M 12,5 L 12,19 M 5,12 L 19,12",
        ["close"] = "M 6,6 L 18,18 M 18,6 L 6,18",
        ["loading"] = "M 6,3 L 18,3 M 6,21 L 18,21 M 7,3 C 7,8 12,10 12,12 C 12,14 7,16 7,21 M 17,3 C 17,8 12,10 12,12 C 12,14 17,16 17,21",
        ["private"] = "M 3,14 A 3.5,3.5 0 1 0 10,14 A 3.5,3.5 0 1 0 3,14 Z M 14,14 A 3.5,3.5 0 1 0 21,14 A 3.5,3.5 0 1 0 14,14 Z M 10,14 C 11,13 13,13 14,14 M 2,10 L 22,10 M 6,10 L 8,4.5 L 16,4.5 L 18,10",
        ["split"] = "M 3,5 L 21,5 L 21,19 L 3,19 Z M 12,5 L 12,19",
        ["search"] = "M 10.5,4 A 6.5,6.5 0 1 1 10.49,4 Z M 15.5,15.5 L 20.5,20.5",
        ["share"] = "M 18,3.5 A 2.5,2.5 0 1 1 17.99,3.5 Z M 6,9.5 A 2.5,2.5 0 1 1 5.99,9.5 Z M 18,15.5 A 2.5,2.5 0 1 1 17.99,15.5 Z M 8.2,10.8 L 15.8,6.7 M 8.2,13.2 L 15.8,17.3",
        ["bluetooth"] = "M 7,7 L 17,17 L 12,21 L 12,3 L 17,7 L 7,17",
        ["printer"] = "M 6,9 L 6,3 L 18,3 L 18,9 M 6,17 L 4,17 L 4,9 L 20,9 L 20,17 L 18,17 M 6,14 L 18,14 L 18,21 L 6,21 Z",
        ["apps"] = "M 4,4 L 10,4 L 10,10 L 4,10 Z M 14,4 L 20,4 L 20,10 L 14,10 Z M 4,14 L 10,14 L 10,20 L 4,20 Z M 14,14 L 20,14 L 20,20 L 14,20 Z",
        ["accessibility"] = "M 12,3 A 1.5,1.5 0 1 1 11.99,3 Z M 5,8 L 19,8 M 12,8 L 12,14 L 8,21 M 12,14 L 16,21",
        ["info"] = "M 12,3 A 9,9 0 1 1 11.99,3 Z M 12,11 L 12,16.5 M 12,7.5 L 12.01,7.5",
        ["lock"] = "M 6,11 L 18,11 L 18,21 L 6,21 Z M 8.5,11 L 8.5,7.5 C 8.5,5 10,3.5 12,3.5 C 14,3.5 15.5,5 15.5,7.5 L 15.5,11",
        ["power"] = "M 12,3 L 12,11 M 7,6.5 A 7.5,7.5 0 1 0 17,6.5",
        ["download"] = "M 12,4 L 12,15 M 7,10 L 12,15 L 17,10 M 5,20 L 19,20",
        ["home"] = "M 3.5,10.5 L 12,3.5 L 20.5,10.5 M 5.5,9 L 5.5,20 L 10,20 L 10,14 L 14,14 L 14,20 L 18.5,20 L 18.5,9",
        ["workspaces"] = "M 3,6 L 11,6 L 11,12 L 3,12 Z M 13,6 L 21,6 L 21,12 L 13,12 Z M 3,14 L 11,14 L 11,20 L 3,20 Z M 13,14 L 21,14 L 21,20 L 13,20 Z",
        ["moon"] = "M 20,14.5 A 8.5,8.5 0 1 1 9.5,4 A 7,7 0 0 0 20,14.5 Z",
        ["sun"] = "M 12,8 A 4,4 0 1 1 11.99,8 Z M 12,2.5 L 12,4.5 M 12,19.5 L 12,21.5 M 2.5,12 L 4.5,12 M 19.5,12 L 21.5,12 M 5.3,5.3 L 6.7,6.7 M 17.3,17.3 L 18.7,18.7 M 5.3,18.7 L 6.7,17.3 M 17.3,6.7 L 18.7,5.3",
        ["battery"] = "M 3,8 L 19,8 L 19,16 L 3,16 Z M 21,10.5 L 21,13.5 M 6,11 L 6,13",
        ["bolt"] = "M 13,3 L 5,13.5 L 11.5,13.5 L 10.5,21 L 19,10 L 12.5,10 Z",
        ["keyboard"] = "M 3,6 L 21,6 L 21,18 L 3,18 Z M 7,10 L 7.01,10 M 11,10 L 11.01,10 M 15,10 L 15.01,10 M 7,14 L 17,14",
        ["star-filled"] = "M 12,3 L 14.8,8.8 L 21,9.6 L 16.5,14 L 17.6,20.3 L 12,17.3 L 6.4,20.3 L 7.5,14 L 3,9.6 L 9.2,8.8 Z",
        ["warning"] = "M 12,3.5 L 21.5,20 L 2.5,20 Z M 12,10 L 12,14 M 12,17 L 12.01,17",
        ["shield"] = "M 12,3 L 19.5,6 L 19.5,11.5 C 19.5,16 16.5,19.5 12,21 C 7.5,19.5 4.5,16 4.5,11.5 L 4.5,6 Z",
        ["book"] = "M 3,5 C 6,4 9,4 12,6 C 15,4 18,4 21,5 L 21,19 C 18,18 15,18 12,20 C 9,18 6,18 3,19 Z M 12,6 L 12,20",
        ["more"] = "M 5,12 L 5.01,12 M 12,12 L 12.01,12 M 19,12 L 19.01,12",
        ["bug"] = "M 8,9 C 8,6.5 9.8,5 12,5 C 14.2,5 16,6.5 16,9 L 16,15 C 16,17.5 14.2,19.5 12,19.5 C 9.8,19.5 8,17.5 8,15 Z M 12,9 L 12,19.5 M 4,10 L 8,11 M 4,15 L 8,14.5 M 20,10 L 16,11 M 20,15 L 16,14.5 M 9.5,5.5 L 8,3 M 14.5,5.5 L 16,3",
        ["cloud"] = "M 7,18.5 L 17.5,18.5 A 4,4 0 0 0 17.8,10.5 A 6,6 0 0 0 6.4,9.6 A 4.5,4.5 0 0 0 7,18.5 Z",
        ["server"] = "M 4,4 L 20,4 L 20,10 L 4,10 Z M 4,14 L 20,14 L 20,20 L 4,20 Z M 7.5,7 L 8.5,7 M 7.5,17 L 8.5,17 M 12,7 L 16.5,7 M 12,17 L 16.5,17",
        ["usb"] = "M 9,2 L 15,2 L 15,7 L 9,7 Z M 7,7 L 17,7 L 17,19 C 17,20.5 16,22 14,22 L 10,22 C 8,22 7,20.5 7,19 Z M 11,4 L 11,5 M 13,4 L 13,5",
        ["eject"] = "M 12,4.5 L 19,13 L 5,13 Z M 5,17.5 L 19,17.5",
        ["panel"] = "M 3,4.5 L 21,4.5 L 21,19.5 L 3,19.5 Z M 15,4.5 L 15,19.5 M 17,8.5 L 19,8.5 M 17,11.5 L 19,11.5",
        ["rename"] = "M 3,7 L 13,7 M 3,12 L 10,12 M 3,17 L 8,17 M 18.5,5.5 L 20.5,7.5 L 13,15 L 10.5,15.5 L 11,13 Z",
    };

    private static readonly HashSet<string> Filled = new() { "record", "play", "star-filled" };

    /// <summary>Emoji that older parts of the shell still send, mapped to icons.</summary>
    private static readonly Dictionary<string, string> FromEmoji = new()
    {
        ["📋"] = "clipboard", ["📝"] = "document", ["📊"] = "sheet", ["📽"] = "slides", ["🎬"] = "film", ["🏓"] = "game",
        ["🌐"] = "globe", ["💬"] = "chat", ["📁"] = "folder", ["🧮"] = "calculator", ["🗒"] = "note", ["🖼"] = "image",
        ["📷"] = "camera", ["📅"] = "calendar", ["⚙"] = "settings", ["⌨"] = "terminal", ["🎨"] = "palette", ["⏱"] = "clock",
        ["📈"] = "activity", ["🔔"] = "bell", ["🔕"] = "bell-off", ["📶"] = "wifi", ["🔌"] = "wired", ["🚫"] = "offline",
        ["🔇"] = "volume-mute", ["🔈"] = "volume-low", ["🔉"] = "volume-mid", ["🔊"] = "volume", ["🖥"] = "monitor",
        ["👤"] = "user", ["🔄"] = "refresh", ["🔴"] = "record", ["▶"] = "play", ["⏸"] = "pause", ["★"] = "star-filled", ["☆"] = "star", ["⚠"] = "warning", ["🛡"] = "shield", ["📖"] = "book", ["⋯"] = "more",
        ["＋"] = "plus", ["⏳"] = "loading", ["🕶"] = "private", ["🔍"] = "search", ["⬇"] = "download", ["🏠"] = "home",
        ["🖨"] = "printer", ["ℹ"] = "info", ["🔒"] = "lock", ["⏻"] = "power",
        ["🌙"] = "moon", ["☀"] = "sun", ["🔋"] = "battery", ["⚡"] = "bolt",
    };

    public static bool Has(string? name) => name != null && Paths.ContainsKey(name);

    /// <summary>Icon name for an emoji or an icon name, or null.</summary>
    public static string? Resolve(string? glyphOrName)
    {
        if (string.IsNullOrEmpty(glyphOrName)) return null;
        if (Paths.ContainsKey(glyphOrName)) return glyphOrName;
        var g = glyphOrName.Replace("️", "");
        return FromEmoji.TryGetValue(g, out var n) ? n : null;
    }

    /// <summary>A vector icon. <paramref name="brush"/> null = the surrounding text colour.</summary>
    public static Control Create(string name, double size = 18, IBrush? brush = null, double strokeWidth = 2)
    {
        var path = new ShapePath
        {
            Data = Geometry.Parse(Paths.TryGetValue(name, out var d) ? d : Paths["info"]),
            StrokeThickness = strokeWidth,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        if (brush != null)
        {
            path.Stroke = brush;
            if (Filled.Contains(name)) path.Fill = brush;
        }
        else
        {
            // follow the foreground of whatever the icon sits in (buttons, menu items, dark / light themes)
            path.Bind(Shape.StrokeProperty, path.GetObservable(TextElement.ForegroundProperty));
            if (Filled.Contains(name)) path.Bind(Shape.FillProperty, path.GetObservable(TextElement.ForegroundProperty));
        }
        return new Viewbox
        {
            Width = size, Height = size,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Canvas { Width = 24, Height = 24, Children = { path } }
        };
    }

    /// <summary>An icon for an emoji / icon name, or the text itself when there is no icon for it (letters...).</summary>
    public static Control Glyph(string? glyphOrName, double size = 16, IBrush? brush = null)
    {
        var name = Resolve(glyphOrName);
        if (name != null) return Create(name, size, brush);
        var tb = new TextBlock { Text = glyphOrName ?? "•", FontSize = size * 0.95, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (brush != null) tb.Foreground = brush;
        return tb;
    }
}

/// <summary>XAML: Content="{Binding Glyph, Converter={x:Static svc:IconConverter.White}}".</summary>
public sealed class IconConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly IconConverter White = new(Brushes.White, 16);
    public static readonly IconConverter Default = new(null, 16);
    private readonly IBrush? _brush;
    private readonly double _size;
    public IconConverter(IBrush? brush, double size) { _brush = brush; _size = size; }
    public object? Convert(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        double size = parameter is string s && double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : _size;
        // "name|#RRGGBB" draws the icon in that colour (e.g. file kinds in the File Manager)
        if (value is string str && str.IndexOf('|') is var bar and > 0 && Color.TryParse(str[(bar + 1)..], out var c))
            return Icons.Glyph(str[..bar], size, new SolidColorBrush(c));
        return Icons.Glyph(value as string, size, _brush);
    }
    public object? ConvertBack(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture) => null;
}
