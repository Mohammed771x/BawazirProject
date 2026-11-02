using WordOs.Domain.Analytics;
using WordOs.Domain.Common;
using WordOs.Domain.Users;
using WordOs.Domain.Words;

namespace WordOs.Api.Admin;

/// <summary>
/// One learner's one day (brief page 3, ADR-132): what the activity calendar
/// opens when a day is clicked.
/// </summary>
/// <remarks>
/// A view over the same rows as the timeline and the 360 — nothing is stored
/// for it. The day is the reporting day (ADR-052), so it matches the square
/// that was clicked. App time and app opens come from the phone and are null,
/// not zero, when the phone sent nothing that day.
/// </remarks>
public static class IntelDay
{
    public static object For(IntelDataset data, User user, DateOnly day)
    {
        bool On(DateTimeOffset t) => data.Day(t) == day;

        var events = data.EventsOf(user.Id).Where(e => On(e.OccurredAt)).ToList();
        var sessions = data.SessionsOf(user.Id).Where(s => On(s.StartedAt)).ToList();
        var real = sessions.Where(s => !s.IsPractice).ToList();
        var words = data.WordsOf(user.Id);
        var decisions = words.SelectMany(w => w.Events)
            .Where(e => On(e.CreatedAt) && e.Type is WordEventType.SkillPassed or WordEventType.SkillFailed).ToList();
        var screen = IntelMetrics.ScreenTime(data.EventsOf(user.Id));
        var abandoned = events.Count(e => e.Name == AnalyticsEventNames.SessionAbandoned);
        var client = events.Any(e => e.Source == AnalyticsSource.Client);

        var items = sessions.SelectMany(s => s.Items).Where(i => i.Attempts > 0).ToList();
        var firstKnown = items.Where(i => i.FirstAttemptCorrect is not null).ToList();

        double? Ms(IEnumerable<Domain.Sessions.SkillSession> list) =>
            list.Select(s => IntelMetrics.LearningMs(data, s, screen)).OfType<double>().ToList() is { Count: > 0 } t ? t.Sum() : null;

        var skills = IntelMetrics.Skills.Select(skill =>
        {
            var mine = sessions.Where(s => s.Skill == skill).ToList();
            var its = mine.SelectMany(s => s.Items).Where(i => i.Attempts > 0).ToList();
            var known = its.Where(i => i.FirstAttemptCorrect is not null).ToList();
            var dec = decisions.Where(e => e.Skill == skill).ToList();
            return new
            {
                skill = IntelMetrics.Wire(skill),
                sessions = mine.Count(s => !s.IsPractice),
                practice = mine.Count(s => s.IsPractice),
                completed = mine.Count(s => s.IsComplete),
                abandoned = events.Count(e => e.Name == AnalyticsEventNames.SessionAbandoned && e.Skill == skill),
                learningMs = Ms(mine),
                questions = its.Count,
                attempts = its.Sum(i => i.Attempts),
                firstAttemptAccuracy = IntelMetrics.Ratio(known.Count(i => i.FirstAttemptCorrect == true), known.Count),
                passed = dec.Count(e => e.Type == WordEventType.SkillPassed),
                failed = dec.Count(e => e.Type == WordEventType.SkillFailed),
                levels = mine.Select(s => s.LevelUsed.ToWire()).Distinct().ToList(),
            };
        }).Where(s => s.sessions + s.practice + s.abandoned + s.passed + s.failed > 0).ToList();

        var timeline = IntelTimeline.For(data, user, verbose: true, limit: int.MaxValue)
            .Where(e => On(e.At)).ToList();

        return new
        {
            date = day.ToString("yyyy-MM-dd"),
            phoneReported = client,
            summary = new
            {
                appOpens = client ? events.Count(e => e.Name == AnalyticsEventNames.AppOpened) : (int?)null,
                appSessions = client ? events.Select(e => e.AppSessionId).Where(a => a != null).Distinct().Count() : (int?)null,
                appMs = client ? events.Where(e => e.Name == AnalyticsEventNames.AppBackgrounded).Sum(e => (double)(e.DurationMs ?? 0)) : (double?)null,
                learningMs = Ms(sessions),
                sessions = real.Count,
                practice = sessions.Count - real.Count,
                completed = real.Count(s => s.IsComplete),
                abandoned,
                questions = items.Count,
                attempts = items.Sum(i => i.Attempts),
                firstAttemptAccuracy = IntelMetrics.Ratio(firstKnown.Count(i => i.FirstAttemptCorrect == true), firstKnown.Count),
                passed = decisions.Count(e => e.Type == WordEventType.SkillPassed),
                failed = decisions.Count(e => e.Type == WordEventType.SkillFailed),
                wordsAdded = words.Count(w => On(w.AddedAt)),
                enteredActive = words.SelectMany(w => w.Events).Count(e => On(e.CreatedAt) && e.Type == WordEventType.EnteredActive),
                reviews = data.ReviewsOf(user.Id).Count(r => On(r.StartedAt) && !r.IsPractice),
                translations = events.Count(e => e.Name == AnalyticsEventNames.TranslationOpened),
                replays = events.Count(e => e.Name == AnalyticsEventNames.AudioReplayed),
                hints = events.Count(e => e.Name == AnalyticsEventNames.HintUsed),
                notificationsOpened = events.Count(e => e.Name == AnalyticsEventNames.NotificationOpened),
            },
            skills,
            wordsAdded = words.Where(w => On(w.AddedAt)).OrderBy(w => w.AddedAt)
                .Select(w => new { id = w.Id, text = w.Text, deleted = w.State == WordState.Deleted }).ToList(),
            events = timeline,
        };
    }
}
