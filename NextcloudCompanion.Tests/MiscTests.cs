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
}
