import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../api/api_providers.dart';
import '../l10n/app_strings.dart';
import '../models/models.dart';
import '../storage/preferences_providers.dart';
import 'notification_scheduler.dart';

/// The platform side of notifications. Overridden in tests with a fake, which
/// is the only way to assert what the app actually asked the phone to do.
final notificationSchedulerProvider = Provider<NotificationScheduler>(
  (ref) => DeviceNotificationScheduler(),
);

/// Turns the server's reminder plan into notifications on this phone (ADR-076).
///
/// The division of labour is the whole design. The server decides which days
/// get a reminder and what each one is about, because that depends on the
/// pipeline and rule R1 puts it there. This turns those decisions into the
/// sentences a learner reads, in their language, and hands them to the OS.
/// Neither half can be moved into the other: the server does not know the
/// language, and the phone does not know the pipeline.
class ReminderController {
  ReminderController(this._ref);

  final Ref _ref;

  /// Fetches the plan and replaces what is pending.
  ///
  /// Called on sign-in and whenever the app comes back to the foreground —
  /// which is also what keeps a daily learner from ever reaching the end of the
  /// week the server hands out.
  ///
  /// Never throws. A reminder that could not be scheduled is a small loss; a
  /// learner blocked from their hub because the reminders endpoint was slow is
  /// a large one. Every failure here is swallowed on purpose.
  Future<void> refresh() async {
    final preferences = _ref.read(appPreferencesProvider);
    final scheduler = _ref.read(notificationSchedulerProvider);

    if (!preferences.remindersEnabled) {
      await _quietly(scheduler.cancelAll);
      return;
    }

    try {
      // Permission first, and every time: a learner can revoke it in system
      // settings between two launches, and scheduling into a revoked permission
      // succeeds silently and delivers nothing.
      if (!await scheduler.initialize()) return;

      final reminders = await _ref.read(wordOsApiProvider).dailyReminders();
      await scheduler.schedule(_notificationsFrom(reminders));
    } catch (error, stack) {
      // Reported, not shown. Nothing the learner is doing depends on this.
      if (kDebugMode) debugPrintStack(label: '$error', stackTrace: stack);
    }
  }

  /// Stops reminding, and forgets what was pending.
  Future<void> disable() async {
    await _ref.read(appPreferencesProvider).setRemindersEnabled(false);
    await _quietly(_ref.read(notificationSchedulerProvider).cancelAll);
  }

  /// Starts reminding again, and schedules immediately rather than waiting for
  /// the next launch — a switch that does nothing until tomorrow reads as
  /// broken.
  Future<void> enable() async {
    await _ref.read(appPreferencesProvider).setRemindersEnabled(true);
    await refresh();
  }

  List<ScheduledNotification> _notificationsFrom(List<DailyReminder> reminders) {
    final s = _ref.read(stringsProvider);
    final now = DateTime.now();
    final scheduled = <ScheduledNotification>[];

    for (final reminder in reminders) {
      final at = reminder.localTime;

      // The server drops times that have already gone by, but it drops them
      // against *its* idea of the day (one product-wide offset). A phone in
      // another timezone can still be handed a past one, and a past
      // notification either fires the instant it is set or is dropped without a
      // word — neither of which is a reminder.
      if (!at.isAfter(now)) continue;

      scheduled.add(ScheduledNotification(
        // Position in the list. Unique within this pass, which is all an id
        // has to be: every pass cancels what it replaces.
        id: scheduled.length,
        title: switch (reminder.slot) {
          ReminderSlot.morning => s.reminderTitleMorning,
          ReminderSlot.evening => s.reminderTitleEvening,
        },
        body: switch (reminder.kind) {
          ReminderKind.wordsDue => s.reminderWordsDue(reminder.count),
          ReminderKind.nothingDue => s.reminderNothingDue(reminder.count),
          ReminderKind.noWords => s.reminderNoWords,
        },
        at: at,
      ));
    }

    return scheduled;
  }

  Future<void> _quietly(Future<void> Function() action) async {
    try {
      await action();
    } catch (_) {
      // See `refresh`: nothing here is worth a failure the learner can see.
    }
  }
}

final reminderControllerProvider = Provider<ReminderController>(
  ReminderController.new,
);
