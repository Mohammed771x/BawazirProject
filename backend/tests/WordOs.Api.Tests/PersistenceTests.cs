using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Lexicon;
using WordOs.Domain.Users;
using WordOs.Domain.Words;

namespace WordOs.Api.Tests;

/// <summary>
/// Integration tests against a real PostgreSQL database created from the
/// migrations.
/// </summary>
/// <remarks>
/// These verify what only a real database can: unique indexes, check
/// constraints, cascade deletes, column lengths, and that the aggregate
/// round-trips through EF Core with its private setters and backing fields
/// intact.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PersistenceTests(PostgresFixture db)
{
    private static readonly WordOsConfiguration Config = new();
    private static readonly DateTimeOffset T0 =
        new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    private static User NewUser(string email = "learner@test.dev") =>
        User.Register(email, "argon2id$dummy-hash", "Learner", Config, T0);

    private static Word NewWord(Guid userId, string senseId = "sense-1") =>
        Word.Add(userId, senseId, "research", "بحث علمي",
            "careful study to discover new facts", "noun",
            CefrLevel.B1, Config, T0);

    [SkippableFact]
    public async Task The_schema_was_created_by_migrations_and_all_tables_exist()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        await using var context = db.CreateContext();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.NotEmpty(applied);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());

        // Every DbSet must be queryable — a missing table throws here. The
        // counts are not asserted to be zero: the whole collection shares one
        // database, so other tests legitimately leave rows behind.
        Assert.True(await context.Users.CountAsync() >= 0);
        Assert.True(await context.Words.CountAsync() >= 0);
        Assert.True(await context.LexiconEntries.CountAsync() >= 0);
        Assert.True(await context.SkillLevels.CountAsync() >= 0);
        Assert.True(await context.WordEvents.CountAsync() >= 0);
        Assert.True(await context.WordSkillStates.CountAsync() >= 0);
        Assert.True(await context.LevelChanges.CountAsync() >= 0);
        Assert.True(await context.UserInterests.CountAsync() >= 0);
    }

    [SkippableFact]
    public async Task A_registered_user_round_trips_with_five_skill_levels()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var user = NewUser($"roundtrip-{Guid.NewGuid():N}@test.dev");

        await using (var write = db.CreateContext())
        {
            write.Users.Add(user);
            await write.SaveChangesAsync();
        }

        await using var read = db.CreateContext();
        var loaded = await read.Users
            .Include(u => u.SkillLevels)
            .SingleAsync(u => u.Id == user.Id);

        Assert.Equal(5, loaded.SkillLevels.Count);
        Assert.Equal(UserRole.User, loaded.Role);

        // Spelling persists with NULL levels (ADR-008) and comes back as null,
        // not as a sentinel A1.
        var spelling = loaded.LevelFor(SkillType.Spelling);
        Assert.Null(spelling.SystemAssessedLevel);
        Assert.Null(spelling.UserSelectedLevel);
        Assert.False(spelling.CarriesCefrLevel);

        Assert.NotNull(loaded.LevelFor(SkillType.Reading));
    }

    [SkippableFact]
    public async Task The_duplicate_rule_is_enforced_by_the_database_not_only_code()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var user = NewUser($"dup-{Guid.NewGuid():N}@test.dev");
        await using var context = db.CreateContext();
        context.Users.Add(user);
        context.Words.Add(NewWord(user.Id, "sense-duplicate"));
        await context.SaveChangesAsync();

        // Same learner, same sense — the unique index must refuse it even if
        // application-level checks were bypassed entirely.
        context.Words.Add(NewWord(user.Id, "sense-duplicate"));

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync());
        Assert.Contains("unique", error.InnerException?.Message ?? "",
            StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task The_same_word_in_a_different_sense_is_allowed()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var user = NewUser($"senses-{Guid.NewGuid():N}@test.dev");
        await using var context = db.CreateContext();
        context.Users.Add(user);

        // book = كتاب and book = يحجز are different synsets, so two rows.
        context.Words.Add(Word.Add(user.Id, "oewn-book-n", "book", "كتاب",
            "a written work", "noun", CefrLevel.A1, Config, T0));
        context.Words.Add(Word.Add(user.Id, "oewn-book-v", "book", "يحجز",
            "to reserve", "verb", CefrLevel.A2, Config, T0));

        await context.SaveChangesAsync();

        var count = await context.Words.CountAsync(w => w.UserId == user.Id);
        Assert.Equal(2, count);
    }

    [SkippableFact]
    public async Task Two_learners_may_each_hold_the_same_sense()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var a = NewUser($"a-{Guid.NewGuid():N}@test.dev");
        var b = NewUser($"b-{Guid.NewGuid():N}@test.dev");

        await using var context = db.CreateContext();
        context.Users.AddRange(a, b);
        context.Words.Add(NewWord(a.Id, "sense-shared"));
        context.Words.Add(NewWord(b.Id, "sense-shared"));

        // The unique index is (UserId, SenseId) — scoped per learner.
        await context.SaveChangesAsync();

        Assert.Equal(2, await context.Words
            .CountAsync(w => w.SenseId == "sense-shared"));
    }

    [SkippableFact]
    public async Task Email_is_unique_across_users()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var email = $"unique-{Guid.NewGuid():N}@test.dev";
        await using var context = db.CreateContext();
        context.Users.Add(NewUser(email));
        await context.SaveChangesAsync();

        context.Users.Add(NewUser(email));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task The_daily_target_range_is_a_check_constraint()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var user = NewUser($"target-{Guid.NewGuid():N}@test.dev");
        await using var context = db.CreateContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        // Bypass the domain clamp and write directly, the way a bug or a bad
        // migration would. The database must still refuse it.
        var affected = await context.Database.ExecuteSqlAsync(
            $"""
             UPDATE user_skill_levels
             SET "DailyTargetWords" = 99
             WHERE "UserId" = {user.Id}
             """).ContinueWith(t => t.IsFaulted ? -1 : t.Result);

        Assert.Equal(-1, affected);
    }

    [SkippableFact]
    public async Task A_word_persists_its_five_skill_states_and_event_history()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var user = NewUser($"pipeline-{Guid.NewGuid():N}@test.dev");
        var word = NewWord(user.Id, $"sense-{Guid.NewGuid():N}");
        word.ApplySessionResult(SkillType.Reading, passed: true, Config, T0);

        await using (var write = db.CreateContext())
        {
            write.Users.Add(user);
            write.Words.Add(word);
            await write.SaveChangesAsync();
        }

        await using var read = db.CreateContext();
        var loaded = await read.Words
            .Include(w => w.Skills)
            .Include(w => w.Events)
            .SingleAsync(w => w.Id == word.Id);

        Assert.Equal(5, loaded.Skills.Count);
        Assert.Equal(SkillStatus.Passed,
            loaded.SkillState(SkillType.Reading).Status);
        Assert.Equal(SkillType.Listening, loaded.CurrentSkill);

        // The gap survived the round trip, not just the status.
        Assert.Equal(
            T0.AddDays(Config.SkillIntervalDays),
            loaded.SkillState(SkillType.Listening).AvailableAt);

        var types = loaded.Events.Select(e => e.Type).ToList();
        Assert.Contains(WordEventType.Added, types);
        Assert.Contains(WordEventType.SkillPassed, types);
    }

    [SkippableFact]
    public async Task Deleting_a_user_cascades_to_their_words_and_history()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var user = NewUser($"cascade-{Guid.NewGuid():N}@test.dev");
        var word = NewWord(user.Id, $"sense-{Guid.NewGuid():N}");

        await using var context = db.CreateContext();
        context.Users.Add(user);
        context.Words.Add(word);
        await context.SaveChangesAsync();

        context.Users.Remove(user);
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.Words.CountAsync(w => w.UserId == user.Id));
        Assert.Equal(0, await context.WordSkillStates
            .CountAsync(s => s.WordId == word.Id));
        Assert.Equal(0, await context.SkillLevels
            .CountAsync(l => l.UserId == user.Id));
    }

    [SkippableFact]
    public async Task Lexicon_prefix_search_uses_the_index_and_finds_senses()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        await using var context = db.CreateContext();
        context.LexiconEntries.AddRange(
            LexiconEntry.Create("s-book-n", "book", "book", "noun",
                "a written work", "كتاب", CefrLevel.A1, 500, "oewn+awn+cefrj", T0),
            LexiconEntry.Create("s-book-v", "book", "book", "verb",
                "to reserve", "يحجز", CefrLevel.A2, 900, "oewn+awn+cefrj", T0),
            LexiconEntry.Create("s-boot-n", "boot", "boot", "noun",
                "footwear", "حذاء", CefrLevel.A2, 2000, "oewn+awn+cefrj", T0),
            LexiconEntry.Create("s-cat-n", "cat", "cat", "noun",
                "a small animal", "قطة", CefrLevel.A1, 300, "oewn+awn+cefrj", T0));
        await context.SaveChangesAsync();

        // Typing "bo" must surface every sense whose word starts with it.
        var matches = await context.LexiconEntries
            .Where(l => l.TextNormalized.StartsWith("bo"))
            .OrderBy(l => l.FrequencyRank)
            .ToListAsync();

        Assert.Equal(3, matches.Count);
        Assert.All(matches, m => Assert.StartsWith("bo", m.TextNormalized));
        Assert.DoesNotContain(matches, m => m.Text == "cat");

        // Both senses of "book" are present and distinguishable.
        var bookSenses = matches.Where(m => m.Text == "book").ToList();
        Assert.Equal(2, bookSenses.Count);
        Assert.Contains(bookSenses, s => s.MeaningAr == "كتاب");
        Assert.Contains(bookSenses, s => s.MeaningAr == "يحجز");
    }

    [SkippableFact]
    public async Task A_lexicon_entry_cannot_exist_without_an_Arabic_meaning()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        Assert.ThrowsAny<ArgumentException>(() => LexiconEntry.Create(
            "s-empty", "word", "word", "noun", "def", "  ",
            CefrLevel.A1, 1, "oewn", T0));

        await Task.CompletedTask;
    }

    [SkippableFact]
    public async Task Arabic_text_survives_the_round_trip_intact()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        const string meaning = "نظام تشغيل";
        var user = NewUser($"arabic-{Guid.NewGuid():N}@test.dev");
        var word = Word.Add(user.Id, $"sense-{Guid.NewGuid():N}",
            "operating system", meaning, "software that manages resources",
            "noun", CefrLevel.B1, Config, T0);

        await using (var write = db.CreateContext())
        {
            write.Users.Add(user);
            write.Words.Add(word);
            await write.SaveChangesAsync();
        }

        await using var read = db.CreateContext();
        var loaded = await read.Words.SingleAsync(w => w.Id == word.Id);

        Assert.Equal(meaning, loaded.Meaning);
    }
}
