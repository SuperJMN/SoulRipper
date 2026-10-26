using SoulRipper.Hints;

namespace SoulRipper.Tests;

public sealed class PlaylistsTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("soulripper-tests-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void Whole_rip_playlist_titles_win_over_per_track_files()
    {
        File.WriteAllText(Path.Combine(dir, "GAME.m3u"),
            "# @TITLE Game\nGAME.gbs::GBS,3,Stage 1 - Forest,0:1:02.5,,0:0:10\nGAME.gbs::GBS,4,Game Over,0:0:04,,0:0:0\n");
        File.WriteAllText(Path.Combine(dir, "01 Stage 1.m3u"),
            "GAME.gbs::GBS,3,Stage 1 - Forest - Composer - Game - (c) 1990,0:1:02.5,,0:0:10\n");

        Assert.Equal(new[]
        {
            new PlaylistTrack(3, "Stage 1 - Forest", 62.5, true),
            new PlaylistTrack(4, "Game Over", 4.0, false),
        }, Playlists.Parse(dir));
    }

    [Fact]
    public void Per_track_files_alone_lose_their_credits_suffix()
    {
        File.WriteAllText(Path.Combine(dir, "01.m3u"), "G.nsf::NSF,0,Title - Composer - Game - (c) 1988,0:0:30,,0:0:10\n");

        Assert.Equal("Title", Playlists.Parse(dir)[0].Title);
    }
}
