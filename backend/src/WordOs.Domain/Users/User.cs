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

    /// <summary>
    /// Where Spelling's hint ladder starts for this learner, or null to start
    /// where their level puts them (ADR-115).
    /// </summary>
    public SpellingClueKind? SpellingHintStart { get; private set; }

    /// <summary>
    /// When the learner proved they read this address, or null if they never
    /// had to (ADR-141). Accounts created before verification existed stay
    /// null — truthfully unverified, and not blocked for it.
    /// </summary>
    public DateTimeOffset? EmailVerifiedAt { get; private set; }

    /// <summary>
    /// The outstanding verification code, hashed like a password; never the
    /// code itself. One at a time: sending a new one replaces it.
    /// </summary>
    public string? EmailCodeHash { get; private set; }

    public DateTimeOffset? EmailCodeSentAt { get; private set; }

    public DateTimeOffset? EmailCodeExpiresAt { get; private set; }

    /// <summary>Wrong guesses against the outstanding code.</summary>
    public int EmailCodeAttempts { get; private set; }

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

        // A number is required to create an account (ADR-054): an account the
        // Owner cannot reach is an account that cannot be helped when its
        // learner reports a problem.
        //
        // Checked on the digits rather than the input, because "()" and "- -"
        // are text that contains no number. Enforced here as well as at the
        // endpoint — the same two-layer rule the rest of this domain follows,
        // so a new caller cannot create an unreachable account by forgetting.
        var code = Digits(phoneCountryCode);
        var number = Digits(phoneNumber);

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(number))
        {
            throw new ArgumentException(
                "A phone number is required to create an account.",
                nameof(phoneNumber));
        }

        var user = new User
        {
            Email = email.Trim().ToLowerInvariant(),
            PasswordHash = passwordHash,
            // Names are not restricted to Latin letters. "أحمد سعيد" is a name.
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? "Learner"
                : displayName.Trim(),
            PhoneCountryCode = code,
            PhoneNumber = number,
            Role = role,
            CreatedAt = now,
            // Proving the address comes before anything else when it is
            // required (ADR-141); otherwise onboarding starts where it always did.
            OnboardingStage = config.RequireEmailVerification
                ? OnboardingStage.VerifyEmail
                : OnboardingStage.Interests,
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

    /// <summary>
    /// The band Spelling's content is pitched at. Spelling carries no CEFR band
    /// of its own, so it follows Reading: whether an English definition is a
    /// usable clue is a reading question (ADR-008).
    /// </summary>
    public CefrLevel SpellingContentLevel() =>
        _skillLevels.FirstOrDefault(l => l.Skill == SkillType.Spelling)?.UserSelectedLevel
        ?? _skillLevels.FirstOrDefault(l => l.Skill == SkillType.Reading)?.UserSelectedLevel
        ?? CefrLevel.B1;

    public void RecordLogin(DateTimeOffset now) => LastLoginAt = now;

    /// <summary>
    /// Replaces the stored hash. The caller does the hashing — the domain never
    /// sees a plaintext password (ADR-078).
    /// </summary>
    public void ChangePassword(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    /// <summary>
    /// Moves onboarding on. Never out of <see cref="OnboardingStage.VerifyEmail"/>:
    /// only <see cref="ConfirmEmail"/> leaves it, so no other path — placement
    /// finished by a crafted request, say — can skip proving the address.
    /// </summary>
    public void AdvanceOnboarding(OnboardingStage stage)
    {
        if (OnboardingStage == OnboardingStage.VerifyEmail)
        {
            throw new InvalidOperationException(
                "The email address must be verified before onboarding moves on.");
        }

        OnboardingStage = stage;
    }

    public bool AwaitsEmailVerification => OnboardingStage == OnboardingStage.VerifyEmail;

    /// <summary>
    /// Stores a fresh verification code (hashed by the caller) and forgets any
    /// earlier one, along with its wrong guesses.
    /// </summary>
    public void IssueEmailCode(string codeHash, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        EmailCodeHash = codeHash;
        EmailCodeSentAt = now;
        EmailCodeExpiresAt = expiresAt;
        EmailCodeAttempts = 0;
    }

    /// <summary>Seconds until another code may be sent; zero when it may.</summary>
    public int SecondsUntilEmailCodeResend(DateTimeOffset now, int cooldownSeconds)
    {
        if (EmailCodeSentAt is not { } sent) return 0;
        var wait = sent.AddSeconds(cooldownSeconds) - now;
        return wait <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(wait.TotalSeconds);
    }

    public bool EmailCodeRedeemable(DateTimeOffset now, int maxAttempts) =>
        EmailCodeHash is not null
        && EmailCodeExpiresAt > now
        && EmailCodeAttempts < maxAttempts;

    /// <summary>Counts a wrong guess; the code is spent at the limit.</summary>
    public void RecordEmailCodeFailure() => EmailCodeAttempts++;

    /// <summary>
    /// The address is proved: record it, spend the code, and let onboarding
    /// start where it starts for everyone.
    /// </summary>
    public void ConfirmEmail(DateTimeOffset now)
    {
        EmailVerifiedAt ??= now;
        EmailCodeHash = null;
        EmailCodeExpiresAt = null;
        EmailCodeAttempts = 0;
        if (OnboardingStage == OnboardingStage.VerifyEmail)
            OnboardingStage = OnboardingStage.Interests;
    }

    /// <summary>
    /// Chooses the rung every Spelling word starts on, or null for automatic
    /// (ADR-115).
    /// </summary>
    /// <remarks>
    /// The letter count is refused as a start: it is the last resort, the hint
    /// under all the others, and a word that opened on it would have no hint
    /// left to give.
    /// </remarks>
    public void ChooseSpellingHintStart(SpellingClueKind? start)
    {
        if (start == SpellingClueKind.LetterCount)
        {
            throw new ArgumentException(
                "The letter count is the last hint, not a place to start.",
                nameof(start));
        }

        SpellingHintStart = start;
    }

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
