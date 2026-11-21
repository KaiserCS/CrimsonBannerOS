// SPDX-License-Identifier: GPL-3.0-or-later
using System.Linq;
using RedStarOS.Views;
using Xunit;

namespace RedStarOS.Tests;

public class GamesTests
{
    [Fact]
    public void Blocks_an_upright_bar_clears_two_rows()
    {
        BlocksGame g = null!;
        for (int seed = 0; seed < 100; seed++) { g = new BlocksGame(seed); if (g.Piece == 0) break; }   // a game that starts with the bar
        Assert.Equal(0, g.Piece);
        for (int y = BlocksGame.Rows - 2; y < BlocksGame.Rows; y++)
            for (int x = 0; x < BlocksGame.Cols - 1; x++) g.Well[x, y] = 2;      // two rows, full but for the last column
        Assert.True(g.Rotate(1));
        while (g.Move(1)) { }
        Assert.All(g.Current, c => Assert.Equal(BlocksGame.Cols - 1, c.X));
        g.HardDrop();
        Assert.Equal(2, g.Lines);
        Assert.True(g.Score >= 300);                                   // two rows at level 1, plus the drop's points
    }

    [Fact]
    public void Blocks_pieces_stay_inside_the_well()
    {
        var g = new BlocksGame(seed: 2);
        for (int i = 0; i < 20; i++) g.Move(-1);
        Assert.All(g.Current, c => Assert.InRange(c.X, 0, BlocksGame.Cols - 1));
        for (int i = 0; i < 4; i++) g.Rotate(1);
        Assert.All(g.Current, c => Assert.InRange(c.X, 0, BlocksGame.Cols - 1));
    }

    [Fact]
    public void Blocks_game_ends_when_the_well_is_full()
    {
        var g = new BlocksGame(seed: 3);
        for (int i = 0; i < 200 && !g.Over; i++) g.HardDrop();
        Assert.True(g.Over);
    }

    [Fact]
    public void Mahjongg_deals_136_tiles_four_of_each_face()
    {
        var g = new MahjonggGame(seed: 4);
        Assert.Equal(136, g.Tiles.Count);
        Assert.All(g.Tiles.GroupBy(t => t.Face), grp => Assert.Equal(4, grp.Count()));
    }

    [Fact]
    public void Mahjongg_boards_can_always_be_cleared()
    {
        for (int seed = 0; seed < 5; seed++)
        {
            var g = new MahjonggGame(seed);
            // take pairs by hint; when stuck, the deal promises a way: shuffle keeps it clearable
            for (int i = 0; i < 200 && !g.Won; i++)
            {
                if (g.Hint() is { } h) Assert.True(g.TryRemove(h.Item1, h.Item2));
                else g.Shuffle();
            }
            Assert.True(g.Won, $"board {seed} wasn't cleared");
        }
    }

    [Fact]
    public void Mahjongg_only_free_matching_tiles_go()
    {
        var g = new MahjonggGame(seed: 5);
        var covered = g.Tiles.First(t => t.Layer == 0 && !g.IsFree(t));
        var other = g.Tiles.First(t => t != covered && t.Face == covered.Face);
        Assert.False(g.TryRemove(covered, other));
        Assert.True(g.Undo() == false);
    }

    [Fact]
    public void Gems_boards_start_without_lines_and_with_a_move()
    {
        for (int seed = 0; seed < 10; seed++)
        {
            var g = new GemsGame(seed);
            Assert.Empty(g.Matches());
            Assert.NotNull(g.FindMove());
        }
    }

    [Fact]
    public void Gems_a_swap_must_make_a_line_and_scores_when_it_does()
    {
        var g = new GemsGame(seed: 6);
        var (ax, ay, bx, by) = g.FindMove()!.Value;
        Assert.True(g.TrySwap(ax, ay, bx, by));
        int chain = 0;
        while (g.Settle(++chain).Cleared.Count > 0) { }
        Assert.True(g.Score > 0);
        Assert.Empty(g.Matches());
        Assert.All(Enumerable.Range(0, GemsGame.Size), x => Assert.All(Enumerable.Range(0, GemsGame.Size), y => Assert.InRange(g.Board[x, y], 0, GemsGame.Kinds - 1)));
    }
}

public class GameThemeTests
{
    [Fact]
    public void Every_mahjongg_face_has_its_own_picture_on_gnomes_sheet()
    {
        var spots = Enumerable.Range(0, RedStarOS.Views.MahjonggGame.Faces).Select(RedStarOS.Views.MahjonggView.SheetIndex).ToList();
        Assert.Equal(spots.Count, spots.Distinct().Count());
        Assert.All(spots, i => Assert.InRange(i, 0, 41));
        Assert.Equal(12, RedStarOS.Views.MahjonggView.SheetIndex(27));     // East wind
        Assert.Equal(37, RedStarOS.Views.MahjonggView.SheetIndex(33));     // white dragon
    }

    [Fact]
    public void Pong_levels_get_harder_step_by_step()
    {
        var l = RedStarOS.Views.PongWindow.Levels;
        for (int i = 1; i < l.Length; i++)
        {
            Assert.True(l[i].Ball > l[i - 1].Ball);
            Assert.True(l[i].AiSpeed > l[i - 1].AiSpeed);
            Assert.True(l[i].AiError < l[i - 1].AiError);
        }
    }
}
