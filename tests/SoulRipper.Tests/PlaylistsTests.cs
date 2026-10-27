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
    public void Nsf_playlists_count_tracks_from_one()
    {
        File.WriteAllText(Path.Combine(dir, "G.m3u"),
            "G.nsf::NSF,1,Title Screen,0:00:29.436,,0:00:00\nG.nsf::NSF,2,Select your Character,0:00:54.792,,0:00:03.012\n");

        Assert.Equal(new[] { (0, "Title Screen"), (1, "Select your Character") },
            Playlists.Parse(dir).Select(t => (t.Track, t.Title)));
    }

    [Fact]
    public void Nsf_playlists_that_list_a_track_zero_count_from_zero()
    {
        File.WriteAllText(Path.Combine(dir, "G.m3u"), "G.nsf::NSF,0,Intro,0:0:30,,0:0:10\nG.nsf::NSF,1,Stage,0:0:30,,0:0:10\n");

        Assert.Equal(new[] { (0, "Intro"), (1, "Stage") }, Playlists.Parse(dir).Select(t => (t.Track, t.Title)));
    }

    [Fact]
    public void Per_track_files_alone_lose_their_credits_suffix()
    {
        File.WriteAllText(Path.Combine(dir, "01.m3u"), "G.nsf::NSF,0,Title - Composer - Game - (c) 1988,0:0:30,,0:0:10\n");

        Assert.Equal("Title", Playlists.Parse(dir)[0].Title);
    }
}
