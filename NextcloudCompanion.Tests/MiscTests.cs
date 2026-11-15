namespace NextcloudCompanion.Tests;

/// <summary>The meeting reminder in the iCalendar text, and the command-line handling.</summary>
public sealed class MiscTests
{
    private static string Ics(int? reminder) => CalendarClient.BuildIcs("uid-1", new MeetingDraft
    {
        Title = "Planning",
        StartLocal = new DateTime(2026, 11, 12, 9, 0, 0),
        EndLocal = new DateTime(2026, 11, 12, 10, 0, 0),
        ReminderMinutesBefore = reminder,
        OrganizerName = "Me",
        OrganizerEmail = "me@example.com",
        Invitees = []
    });

    [Fact]
    public void AReminderOfFifteenMinutesBecomesAnAlarmBeforeTheStart()
    {
        string ics = Ics(15);
        Assert.Contains("BEGIN:VALARM", ics);
        Assert.Contains("ACTION:DISPLAY", ics);
        Assert.Contains("TRIGGER:-PT15M", ics);
        Assert.Contains("END:VALARM", ics);
        // The alarm belongs inside the event.
        Assert.True(ics.IndexOf("BEGIN:VALARM", StringComparison.Ordinal) < ics.IndexOf("END:VEVENT", StringComparison.Ordinal));
    }

    [Fact]
    public void NoReminderMeansNoAlarm()
    {
        Assert.DoesNotContain("VALARM", Ics(null));
    }

    [Fact]
    public void TriggerValuesUseTheRightUnits()
    {
        Assert.Equal("PT0S", CalendarClient.AlarmTrigger(0));
        Assert.Equal("-PT5M", CalendarClient.AlarmTrigger(5));
        Assert.Equal("-PT15M", CalendarClient.AlarmTrigger(15));
        Assert.Equal("-PT90M", CalendarClient.AlarmTrigger(90));
        Assert.Equal("-PT2H", CalendarClient.AlarmTrigger(120));
        Assert.Equal("-P1D", CalendarClient.AlarmTrigger(1440));
        Assert.Equal("-P2D", CalendarClient.AlarmTrigger(2880));
    }

    [Fact]
    public void CommandLinesAreSplitLikeWindowsDoes()
    {
        var parts = CommandLine.Split("\"C:\\Program Files\\Nextcloud\\nextcloud.exe\" --confdir \"D:\\My Config\" --logdebug a\\\"b");
        Assert.Equal(5, parts.Count);
        Assert.Equal(@"C:\Program Files\Nextcloud\nextcloud.exe", parts[0]);
        Assert.Equal(@"D:\My Config", parts[2]);
        Assert.Equal("a\"b", parts[4]);
        Assert.Equal(0, CommandLine.Split("   ").Count);
    }

    [Fact]
    public void OnlyTheConfigurationContextIsCarriedOver()
    {
        var args = ClientRecoveryService.ContextArguments(
            "\"C:\\x\\nextcloud.exe\" --background --confdir C:\\cfg --logfile=C:\\l.txt --logexpire 24 --logflush --showsettings file.txt");
        Assert.Equal("--confdir|C:\\cfg|--logfile=C:\\l.txt|--logexpire|24|--logflush", string.Join("|", args));
        Assert.Equal(0, ClientRecoveryService.ContextArguments(null).Count);
    }

    [Fact]
    public void TheSystemClockIsMonotonicAndTracksRealTime()
    {
        long a = SystemClock.Instance.MonotonicMilliseconds;
        Thread.Sleep(300);
        long b = SystemClock.Instance.MonotonicMilliseconds;
        Assert.True(b - a >= 250 && b - a < 2000, $"300 ms of real time measured as {b - a} ms");
    }

    private sealed class RecordingSink : INotificationSink
    {
        public List<string> Titles { get; } = [];
        public void Show(string title, string text, NotificationLevel level) => Titles.Add(title);
    }

    [Fact]
    public void ANotificationIsShownAtMostOncePerIntervalForItsKey()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var notifier = new Notifier(sink, clock);

        // A condition that flaps every four seconds for ten minutes asks for the same notice again and again.
        for (int i = 0; i < 150; i++)
        {
            notifier.Show("stall", "stuck", "text", NotificationLevel.Warning, TimeSpan.FromMinutes(90));
            clock.Advance(TimeSpan.FromSeconds(4));
        }
        Assert.Equal(1, sink.Titles.Count);

        clock.Advance(TimeSpan.FromMinutes(91));
        Assert.True(notifier.Show("stall", "stuck", "text", NotificationLevel.Warning, TimeSpan.FromMinutes(90)), "shown again after the interval");
        Assert.Equal(2, sink.Titles.Count);
    }

    [Fact]
    public void TheNumberOfNotificationsPerMinuteIsCapped()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var notifier = new Notifier(sink, clock);

        for (int i = 0; i < 30; i++)                      // thirty different keys within seconds
        {
            notifier.Show("k" + i, "t" + i, "x", NotificationLevel.Info, TimeSpan.Zero);
            clock.Advance(TimeSpan.FromMilliseconds(500));
        }
        Assert.Equal(Notifier.MaxPerMinute, sink.Titles.Count, "no more than the cap within a minute");

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(notifier.Show("later", "t", "x", NotificationLevel.Info, TimeSpan.Zero), "allowed again afterwards");
    }

    [Fact]
    public void ADifferentKeyIsIndependent()
    {
        var clock = new FakeClock();
        var sink = new RecordingSink();
        var notifier = new Notifier(sink, clock);
        notifier.Show("a", "A", "x", NotificationLevel.Info, TimeSpan.FromHours(1));
        notifier.Show("b", "B", "x", NotificationLevel.Info, TimeSpan.FromHours(1));
        Assert.Equal("A,B", string.Join(",", sink.Titles));
    }
}
