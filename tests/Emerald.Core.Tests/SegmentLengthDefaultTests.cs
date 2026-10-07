using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// That a recording really is cut into thirty-second segments.
///
/// Written because changing the default alone did not do it, in three separate ways, and each
/// one would have silently kept two-minute segments on every machine that had ever run Emerald:
///
/// <list type="number">
/// <item>A stored value always beats a property default, and every existing settings file had
/// 120 in it.</item>
/// <item>The recorder runs the stored value through a list of offered lengths and falls back
/// when it is not on it — and 30 was not on it.</item>
/// <item>The fallback itself was 120.</item>
/// </list>
///
/// The segment length is also how long the newest material is unreadable for, since ffmpeg
/// writes the index last, so getting this wrong is not cosmetic.
/// </summary>
public sealed class SegmentLengthDefaultTests
{
    [Fact]
    public void A_fresh_install_records_in_thirty_second_segments()
    {
        Assert.Equal(30, new AppSettings().RecordingSegmentSeconds);
        Assert.Equal(30, AppSettings.DefaultSegmentSeconds);
    }

    [Fact]
    public void A_settings_file_left_on_the_old_default_is_brought_forward()
    {
        var old = new AppSettings { RecordingSegmentSeconds = 120 };

        Assert.Equal(30, AppSettings.Migrate(old).RecordingSegmentSeconds);
    }

    [Fact]
    public void A_length_somebody_chose_is_left_alone()
    {
        // Only the old default is migrated. Anything else is a decision, and moving it would be
        // this code overruling an operator.
        foreach (int chosen in new[] { 30, 60, 300, 600 })
        {
            var settings = new AppSettings { RecordingSegmentSeconds = chosen };

            Assert.Equal(chosen, AppSettings.Migrate(settings).RecordingSegmentSeconds);
        }
    }

    [Fact]
    public void Migrating_twice_changes_nothing_the_second_time()
    {
        var settings = new AppSettings { RecordingSegmentSeconds = 120 };

        AppSettings.Migrate(settings);
        AppSettings.Migrate(settings);

        Assert.Equal(30, settings.RecordingSegmentSeconds);
    }
}
