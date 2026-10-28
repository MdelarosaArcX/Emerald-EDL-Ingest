using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// The moments at which frames were lost, kept so a timeline can mark them.
///
/// Written by two real-time loops and read on every redraw, so what matters is that it holds
/// what it is given, bounds what it keeps, and tells the two kinds of loss apart — a hole in the
/// recording and a frame the transmitter repeated are different facts about the same timeline.
/// </summary>
public sealed class DropMarksTests
{
    private static readonly DateTime Day = new(2026, 10, 14, 5, 23, 23, DateTimeKind.Local);

    [Fact]
    public void A_moment_is_kept_with_its_time_its_count_and_its_kind()
    {
        var marks = new DropMarks();

        marks.Add(Day, 14, DropKind.Capture);

        DropMark only = Assert.Single(marks.All());

        Assert.Equal(Day, only.At);
        Assert.Equal(14, only.Frames);
        Assert.Equal(DropKind.Capture, only.Kind);
    }

    [Fact]
    public void The_two_kinds_say_different_things()
    {
        // A hole in the recording and a repeated frame on air are not the same event, and an
        // operator reading a marker needs to know which they are looking at.
        Assert.Contains("not recorded", new DropMark(Day, 3, DropKind.Capture).Label);
        Assert.Contains("on air", new DropMark(Day, 3, DropKind.OnAir).Label);
    }

    [Fact]
    public void A_loss_of_no_frames_is_not_a_moment()
    {
        var marks = new DropMarks();

        marks.Add(Day, 0, DropKind.Capture);
        marks.Add(Day, -5, DropKind.OnAir);

        Assert.Equal(0, marks.Count);
    }

    [Fact]
    public void Only_the_day_asked_for_comes_back()
    {
        var marks = new DropMarks();

        marks.Add(Day, 2, DropKind.Capture);
        marks.Add(Day.AddDays(-1), 9, DropKind.Capture);
        marks.Add(Day.AddDays(1), 4, DropKind.OnAir);

        DropMark only = Assert.Single(marks.On(Day));
        Assert.Equal(2, only.Frames);

        // A time later the same day is the same day, whatever the hour.
        marks.Add(Day.Date.AddHours(23).AddMinutes(59), 1, DropKind.OnAir);
        Assert.Equal(2, marks.On(Day).Count);
    }

    [Fact]
    public void They_come_back_in_the_order_they_happened()
    {
        var marks = new DropMarks();

        for (int i = 0; i < 10; i++) marks.Add(Day.AddSeconds(i), i + 1, DropKind.Capture);

        Assert.Equal(Enumerable.Range(1, 10), marks.All().Select(m => m.Frames));
    }

    [Fact]
    public void A_plant_losing_frames_all_day_cannot_grow_without_limit()
    {
        var marks = new DropMarks();

        for (int i = 0; i < DropMarks.Limit + 500; i++)
            marks.Add(Day.AddSeconds(i), 1, DropKind.Capture);

        Assert.Equal(DropMarks.Limit, marks.Count);
    }

    [Fact]
    public void The_oldest_are_the_ones_that_go()
    {
        // What is on screen is the recent past, so that is what is worth keeping.
        var marks = new DropMarks();

        for (int i = 0; i < DropMarks.Limit + 10; i++)
            marks.Add(Day.AddSeconds(i), i + 1, DropKind.Capture);

        Assert.Equal(11, marks.All()[0].Frames);
    }

    [Fact]
    public void Clearing_leaves_nothing_behind()
    {
        var marks = new DropMarks();

        marks.Add(Day, 5, DropKind.Capture);
        marks.Clear();

        Assert.Equal(0, marks.Count);
        Assert.Empty(marks.All());
    }

    [Fact]
    public void A_timeline_is_told_when_something_is_marked()
    {
        var marks = new DropMarks();

        int told = 0;
        marks.Changed += () => told++;

        marks.Add(Day, 1, DropKind.Capture);
        Assert.Equal(1, told);

        // Nothing happened, so nothing is said - a redraw on every non-event would be the
        // timeline rebuilding itself for no reason.
        marks.Add(Day, 0, DropKind.Capture);
        Assert.Equal(1, told);

        marks.Clear();
        Assert.Equal(2, told);

        marks.Clear();
        Assert.Equal(2, told);
    }

    [Fact]
    public void Writing_from_several_threads_loses_nothing()
    {
        // The capture loop and the transmitter loop both write to this, and neither may be made
        // to wait on the other for long.
        var marks = new DropMarks();

        Parallel.For(0, 8, t =>
        {
            for (int i = 0; i < 100; i++) marks.Add(Day.AddSeconds(i), 1, DropKind.Capture);
        });

        Assert.Equal(800, marks.Count);
    }
}
