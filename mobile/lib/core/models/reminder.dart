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

  /// What [kind] counts. Words due, words in the pipeline, or zero.
  final int count;

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
      );

  Map<String, dynamic> toJson() => {
        'slot': slot.wire,
        'date':
            '${date.year.toString().padLeft(4, '0')}-${date.month.toString().padLeft(2, '0')}-${date.day.toString().padLeft(2, '0')}',
        'hour': hour,
        'minute': minute,
        'kind': kind.wire,
        'count': count,
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
