using WordOs.Domain.Common;
using WordOs.Domain.Levels;

namespace WordOs.Domain.Users;

/// <summary>A learner, or the system owner.</summary>
public class User
{
    private readonly List<UserInterest> _interests = [];
    private readonly List<SkillLevel> _skillLevels = [];
    private readonly List<LevelChangeRecord> _levelChanges = [];

    private User() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public string Email { get; private set; } = string.Empty;

    /// <summary>
    /// Argon2id/bcrypt hash. The plaintext password never exists outside the
    /// request that set it, and is never logged (docs/07-SECURITY.md §2).
    /// </summary>
    public string PasswordHash { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>
    /// The country calling code, digits only and without the plus — "967",
    /// "966", "91".
    /// </summary>
    /// <remarks>
    /// Stored apart from <see cref="PhoneNumber"/> rather than glued into one
    /// string: the two answer different questions. The calling code says which
    /// country a learner is in — which is a real analytics dimension and a real
    /// formatting input — and recovering it from a concatenated string means
    /// guessing where the prefix ends, which is ambiguous
    /// (+1 vs +1-242, +7 vs +76).
    /// </remarks>
    public string? PhoneCountryCode { get; private set; }

    /// <summary>The national number, digits only, without the calling code.</summary>
    public string? PhoneNumber { get; private set; }

    /// <summary>
    /// Set at seed time only. There is deliberately no client-reachable path to
    /// becoming an <see cref="UserRole.Owner"/> — registration always creates a
    /// <see cref="UserRole.User"/> (docs/07-SECURITY.md §3).
    /// </summary>
    public UserRole Role { get; private set; } = UserRole.User;

    public OnboardingStage OnboardingStage { get; private set; } =
        OnboardingStage.Interests;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public string TimeZone { get; private set; } = "UTC";

    public IReadOnlyList<UserInterest> Interests => _interests;

    public IReadOnlyList<SkillLevel> SkillLevels => _skillLevels;

    public IReadOnlyList<LevelChangeRecord> LevelChanges => _levelChanges;

    public static User Register(
        string email,
        string passwordHash,
        string displayName,
        WordOsConfiguration config,
        DateTimeOffset now,
        UserRole role = UserRole.User,
        string? phoneCountryCode = null,
        string? phoneNumber = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        var user = new User
        {
            Email = email.Trim().ToLowerInvariant(),
            PasswordHash = passwordHash,
            // Names are not restricted to Latin letters. "أحمد سعيد" is a name.
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? "Learner"
                : displayName.Trim(),
            PhoneCountryCode = Digits(phoneCountryCode),
            PhoneNumber = Digits(phoneNumber),
            Role = role,
            CreatedAt = now,
        };

        // Exactly one level row per skill, from the moment the account exists.
        foreach (var skill in config.SkillsOrder)
        {
            user._skillLevels.Add(SkillLevel.Create(user.Id, skill, config));
        }

        return user;
    }

    /// <summary>
    /// Keeps only the digits, so "+967", "967" and "(967)" all store the same
    /// thing and a lookup by country code is a plain equality test.
    /// </summary>
    private static string? Digits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    public SkillLevel LevelFor(SkillType skill) =>
        _skillLevels.Single(l => l.Skill == skill);

    public void RecordLogin(DateTimeOffset now) => LastLoginAt = now;

    public void AdvanceOnboarding(OnboardingStage stage) => OnboardingStage = stage;

    public void ReplaceInterests(IEnumerable<string> interests, DateTimeOffset now)
    {
        _interests.Clear();
        foreach (var interest in interests
                     .Select(i => i.Trim())
                     .Where(i => i.Length > 0)
                     .DistinctBy(i => i.ToLowerInvariant()))
        {
            _interests.Add(UserInterest.Create(Id, interest, now));
        }

        if (OnboardingStage == OnboardingStage.Interests)
            OnboardingStage = OnboardingStage.Placement;
    }

    public void RecordLevelChange(LevelChangeRecord change) =>
        _levelChanges.Add(change);
}

/// <summary>
/// One interest. <see cref="IsCustom"/> marks something the learner typed
/// rather than picked, which is the signal for growing the catalogue.
/// </summary>
public class UserInterest
{
    private UserInterest() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid UserId { get; private set; }

    public string Interest { get; private set; } = string.Empty;

    public bool IsCustom { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Slugs known to the catalogue; anything else is custom.</summary>
    public static readonly IReadOnlySet<string> CatalogueSlugs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "technology", "programming", "ai", "football", "business",
            "entrepreneurship", "economics", "medicine", "travel", "history",
            "science",
        };

    internal static UserInterest Create(
        Guid userId,
        string interest,
        DateTimeOffset now) =>
        new()
        {
            UserId = userId,
            Interest = interest,
            IsCustom = !CatalogueSlugs.Contains(interest),
            CreatedAt = now,
        };
}
