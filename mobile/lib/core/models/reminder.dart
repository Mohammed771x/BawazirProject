/// A daily reminder the phone should raise (ADR-076).
///
/// Everything the notification will say is decided by the server and carried
/// here, because a local notification fires with no network and usually with
/// the app not running — so there is nobody to ask when it goes off. Rule R1
/// holds even for a sentence about the future: the client renders, it does not
/// count.
class DailyReminder {
  const DailyReminder({
    required this.slot,
    required this.date,
    required this.hour,
    required this.minute,
    required this.kind,
    required this.count,
    this.message,
  });

  /// `MORNING` or `EVENING`. It chooses the greeting and nothing else.
  final ReminderSlot slot;

  /// The local calendar date this belongs to.
  final DateTime date;

  /// Local wall-clock time to fire at.
  ///
  /// Wall-clock rather than an instant, deliberately: the phone schedules it in
  /// its own timezone, and a learner who wants a reminder "in the morning"
  /// means the time on their own phone — not an instant computed somewhere
  /// else and translated.
  final int hour;
  final int minute;

  /// What this reminder is about. A stable key, said by this app in the
  /// learner's own language (ADR-035) — never a sentence from the server, which
  /// does not know which language this installation reads.
  final ReminderKind kind;

  /// What the line counts, or zero when it counts nothing.
  ///
  /// What it is counting depends on [message] — words due, days of a streak,
  /// days until the next word ripens — and the message says which, so no line
  /// has to guess what number it was handed.
  final int count;

  /// **Which** of the twenty lines this is (ADR-090).
  ///
  /// Null from a server that predates the catalogue, and null for a key this
  /// build has never heard of — both fall back on [kind], which has only ever
  /// had three values and is not going to grow. That fallback is the reason
  /// both fields are sent: the server ships far more often than the phones do,
  /// and a notification that renders as an empty string is worse than a plain
  /// one.
  final ReminderMessage? message;

  /// When this actually fires, in the device's own timezone.
  DateTime get localTime =>
      DateTime(date.year, date.month, date.day, hour, minute);

  factory DailyReminder.fromJson(Map<String, dynamic> json) => DailyReminder(
        slot: ReminderSlot.fromWire(json['slot'] as String?),
        // Date-only: `DateTime.parse` reads "2026-09-14" as local midnight,
        // which is what the wall-clock hour is then added to.
        date: DateTime.tryParse(json['date'] as String? ?? '') ?? DateTime.now(),
        hour: (json['hour'] as num?)?.toInt() ?? 0,
        minute: (json['minute'] as num?)?.toInt() ?? 0,
        kind: ReminderKind.fromWire(json['kind'] as String?),
        count: (json['count'] as num?)?.toInt() ?? 0,
        message: ReminderMessage.fromWire(json['message'] as String?),
      );

  Map<String, dynamic> toJson() => {
        'slot': slot.wire,
        'date':
            '${date.year.toString().padLeft(4, '0')}-${date.month.toString().padLeft(2, '0')}-${date.day.toString().padLeft(2, '0')}',
        'hour': hour,
        'minute': minute,
        'kind': kind.wire,
        'count': count,
        'message': message?.wire,
      };
}

enum ReminderSlot {
  morning('MORNING'),
  evening('EVENING');

  const ReminderSlot(this.wire);

  final String wire;

  static ReminderSlot fromWire(String? wire) =>
      wire == 'EVENING' ? ReminderSlot.evening : ReminderSlot.morning;
}

/// What a reminder is about.
///
/// Three, because they are three different things to say to somebody. "You have
/// 0 words ready" is true for both a learner who has never added a word and one
/// who finished everything yesterday, and it is the wrong sentence for both.
enum ReminderKind {
  /// Words are due for practice at that moment.
  wordsDue('WORDS_DUE'),

  /// Words are in the pipeline, none of them due yet.
  nothingDue('NOTHING_DUE'),

  /// No vocabulary at all. Nothing to practise because nothing was started.
  noWords('NO_WORDS');

  const ReminderKind(this.wire);

  final String wire;

  static ReminderKind fromWire(String? wire) => switch (wire) {
        'WORDS_DUE' => ReminderKind.wordsDue,
        'NOTHING_DUE' => ReminderKind.nothingDue,
        _ => ReminderKind.noWords,
      };
}

/// The exact line a reminder says — one of twenty (ADR-090).
///
/// A key chosen by the server, said here, because only the server knows the
/// facts that make one line true and another one a lie (ADR-035). Three lines
/// became twenty because a notification is the whole decision about whether the
/// app gets opened today, and the same sentence twice a day for a fortnight
/// stops being read.
enum ReminderMessage {
  wordsDueCount('WORDS_DUE_COUNT'),
  wordsDueOne('WORDS_DUE_ONE'),
  wordsDueFiveMinutes('WORDS_DUE_FIVE_MINUTES'),
  wordsDueMorning('WORDS_DUE_MORNING'),
  wordsDueEvening('WORDS_DUE_EVENING'),
  wordsDueStreak('WORDS_DUE_STREAK'),
  wordsDueStreakAtRisk('WORDS_DUE_STREAK_AT_RISK'),
  wordsDueAfterGoodDay('WORDS_DUE_AFTER_GOOD_DAY'),
  wordsDueWelcomeBack('WORDS_DUE_WELCOME_BACK'),
  wordsDueLevelRose('WORDS_DUE_LEVEL_ROSE'),
  wordsDueAlmostActive('WORDS_DUE_ALMOST_ACTIVE'),
  nothingDueResting('NOTHING_DUE_RESTING'),
  nothingDueAddOne('NOTHING_DUE_ADD_ONE'),
  nothingDueActiveCount('NOTHING_DUE_ACTIVE_COUNT'),
  nothingDueNextOpens('NOTHING_DUE_NEXT_OPENS'),
  noWordsFirst('NO_WORDS_FIRST'),
  noWordsOneADay('NO_WORDS_ONE_A_DAY'),
  reviewReady('REVIEW_READY'),
  reviewMoreWaiting('REVIEW_MORE_WAITING'),
  openTheApp('OPEN_THE_APP');

  const ReminderMessage(this.wire);

  final String wire;

  /// Null rather than a guess for anything unrecognised — the caller then falls
  /// back on the kind, which is a true sentence rather than an invented one.
  static ReminderMessage? fromWire(String? wire) {
    if (wire == null || wire.isEmpty) return null;
    for (final value in ReminderMessage.values) {
      if (value.wire == wire) return value;
    }
    return null;
  }
}
