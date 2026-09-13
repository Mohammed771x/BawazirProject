import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/l10n/app_strings.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/core/notifications/reminder_providers.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/mock_backend/mock_wordos_api.dart';

import 'support/test_harness.dart';

/// The reminders the phone raises when the app is not open (ADR-076).
///
/// The division these tests are really about: the server decides *which* days
/// get a reminder and what each is about, and this app decides how that reads
/// in the learner's language and hands it to the OS. Neither half can do the
/// other's job — the server does not know the language, and the phone does not
/// know the pipeline.
void main() {
  /// A container wired to the mock backend, with the phone faked out.
  ///
  /// `latencyScale: 0` because these await real futures rather than pumping a
  /// widget tree, and the mock's artificial delay only makes them slow.
  Future<(ProviderContainer, FakeNotificationScheduler, InMemoryAppPreferences)>
      signedIn({
    Locale locale = const Locale('en'),
    bool remindersEnabled = true,
  }) async {
    String? token;
    final api = MockWordOsApi(tokenReader: () => token, latencyScale: 0);
    await api.login(email: 'demo@wordos.app', password: 'wordos123').then((a) {
      token = a.token;
    });

    final scheduler = FakeNotificationScheduler();
    final preferences = InMemoryAppPreferences(
      locale: locale,
      remindersEnabled: remindersEnabled,
    );

    final container = ProviderContainer(overrides: [
      appPreferencesProvider.overrideWithValue(preferences),
      wordOsApiProvider.overrideWithValue(api),
      notificationSchedulerProvider.overrideWithValue(scheduler),
    ]);
    addTearDown(container.dispose);

    return (container, scheduler, preferences);
  }

  test('every scheduled reminder is in the future and has something to say',
      () async {
    final (container, scheduler, _) = await signedIn();

    await container.read(reminderControllerProvider).refresh();

    expect(scheduler.scheduled, isNotEmpty);

    final now = DateTime.now();
    for (final notification in scheduler.scheduled) {
      // A past notification either fires the instant it is set — an alert the
      // learner never asked for, at the moment they opened the app — or is
      // dropped without a word. Neither is a reminder.
      expect(notification.at.isAfter(now), isTrue,
          reason: 'scheduled for ${notification.at}, which has gone by');
      expect(notification.title, isNotEmpty);
      expect(notification.body, isNotEmpty);
    }
  });

  test('the ids are unique, or the phone keeps only the last one', () async {
    final (container, scheduler, _) = await signedIn();

    await container.read(reminderControllerProvider).refresh();

    final ids = scheduler.scheduled.map((n) => n.id).toList();
    expect(ids.toSet().length, ids.length);
  });

  test('the count the server sent is the number the learner reads', () async {
    // Rule R1 in the one place it is easiest to break: it would be trivial to
    // count the learner's words here, and it would be wrong the moment the app
    // was closed — the phone has no pipeline to count against.
    final (container, scheduler, _) = await signedIn();
    final reminders = await container.read(wordOsApiProvider).dailyReminders();
    final due = reminders.firstWhere(
      (r) => r.kind == ReminderKind.wordsDue,
      orElse: () => reminders.first,
    );

    await container.read(reminderControllerProvider).refresh();

    if (due.kind == ReminderKind.wordsDue) {
      expect(
        scheduler.scheduled.any((n) => n.body.contains('${due.count}')),
        isTrue,
        reason: 'the number in the notification is the server\'s, not a '
            'number this app worked out',
      );
    }
  });

  test('morning and evening are told apart', () async {
    final (container, scheduler, _) = await signedIn();

    await container.read(reminderControllerProvider).refresh();

    final titles = scheduler.scheduled.map((n) => n.title).toSet();
    // A week of reminders always spans both, whatever hour the suite runs at.
    expect(titles, containsAll(['Good morning', 'Good evening']));
  });

  test('the reminders are written in the language the app is set to', () async {
    final (container, scheduler, _) = await signedIn(locale: const Locale('ar'));

    await container.read(reminderControllerProvider).refresh();

    // The server sends a key and a number and never a sentence (ADR-035): it
    // has no idea which language this installation reads.
    //
    // Not `first`: whether the first reminder of the week is a morning or an
    // evening one depends on the hour the suite happens to run at.
    expect(
      scheduler.scheduled.map((n) => n.title).toSet(),
      containsAll(['صباح الخير', 'مساء الخير']),
    );
    expect(
      scheduler.scheduled.every((n) => !RegExp('[a-zA-Z]').hasMatch(n.title)),
      isTrue,
    );
  });

  test('turning reminders off cancels what is pending', () async {
    final (container, scheduler, preferences) = await signedIn();
    final controller = container.read(reminderControllerProvider);

    await controller.refresh();
    expect(scheduler.scheduled, isNotEmpty);

    await controller.disable();

    expect(preferences.remindersEnabled, isFalse);
    expect(scheduler.scheduled, isEmpty);

    // And a later refresh does not quietly put them back. A switch that only
    // holds until the next launch is a switch that does not work.
    await controller.refresh();
    expect(scheduler.scheduled, isEmpty);
  });

  test('turning them back on schedules immediately', () async {
    // Not on the next launch. A control that appears to do nothing is one the
    // learner presses again, and then decides is broken.
    final (container, scheduler, preferences) =
        await signedIn(remindersEnabled: false);

    await container.read(reminderControllerProvider).enable();

    expect(preferences.remindersEnabled, isTrue);
    expect(scheduler.scheduled, isNotEmpty);
  });

  test('a refused permission schedules nothing', () async {
    // The learner is allowed to say no, and the OS asks them — so this is an
    // ordinary answer rather than a failure. Scheduling into a refused
    // permission succeeds silently and delivers nothing, which would leave the
    // app believing it had reminded somebody.
    final (container, scheduler, _) = await signedIn();
    scheduler.permissionGranted = false;

    await container.read(reminderControllerProvider).refresh();

    expect(scheduler.initialized, isTrue);
    expect(scheduler.scheduled, isEmpty);
  });

  test('a refresh replaces the pending set rather than adding to it', () async {
    final (container, scheduler, _) = await signedIn();
    final controller = container.read(reminderControllerProvider);

    await controller.refresh();
    final first = scheduler.scheduled.length;

    await controller.refresh();

    // Every reminder names a specific day and carries a count that was true
    // when it was written. Two passes must not leave two of each — one of them
    // saying a number the server no longer believes.
    expect(scheduler.scheduled.length, first);
  });

  test('a server that cannot be reached does not break anything', () async {
    // Reminders are the least important thing the app does. A learner blocked
    // from their hub because this endpoint was down would be a far worse bug
    // than a week of missing notifications.
    final container = ProviderContainer(overrides: [
      appPreferencesProvider.overrideWithValue(InMemoryAppPreferences()),
      wordOsApiProvider.overrideWithValue(MockWordOsApi(
        // No token: every call is refused as unauthenticated.
        tokenReader: () => null,
        latencyScale: 0,
      )),
      notificationSchedulerProvider
          .overrideWithValue(FakeNotificationScheduler()),
    ]);
    addTearDown(container.dispose);

    await container.read(reminderControllerProvider).refresh();
  });

  // ── The wording ────────────────────────────────────────────────────────

  test('never having started is a different sentence from nothing being due',
      () {
    // "You have 0 words ready" is true for both and useless to both.
    const en = AppStrings(Locale('en'));
    expect(en.reminderNoWords, isNot(en.reminderNothingDue(0)));
  });

  test('one word is not "1 words"', () {
    const en = AppStrings(Locale('en'));
    const ar = AppStrings(Locale('ar'));

    expect(en.reminderWordsDue(1), contains('1 word is'));
    expect(en.reminderWordsDue(4), contains('4 words are'));
    expect(ar.reminderWordsDue(1), contains('كلمة واحدة'));
    expect(ar.reminderWordsDue(4), contains('4'));
  });

  test('a reminder survives the round trip through JSON', () {
    // The model is the contract. A field renamed on one side and not the other
    // shows up as a notification saying the wrong thing at the wrong hour —
    // and nobody is watching the app when that happens.
    const json = {
      'slot': 'EVENING',
      'date': '2026-09-17',
      'hour': 20,
      'minute': 0,
      'kind': 'WORDS_DUE',
      'count': 4,
    };

    final reminder = DailyReminder.fromJson(json);

    expect(reminder.slot, ReminderSlot.evening);
    expect(reminder.kind, ReminderKind.wordsDue);
    expect(reminder.count, 4);
    expect(reminder.localTime, DateTime(2026, 9, 17, 20));
    expect(reminder.toJson(), json);
  });

  test('an unknown kind is read as the harmless one', () {
    // A server deployed ahead of this app sends a key it has never seen. The
    // wrong sentence is recoverable; a crash on a background refresh is not.
    expect(
      DailyReminder.fromJson(const {'kind': 'SOMETHING_NEW'}).kind,
      ReminderKind.noWords,
    );
  });

  // ── The switch in Settings ─────────────────────────────────────────────

  testWidgets('Settings offers a way to stop being reminded', (tester) async {
    final scheduler = FakeNotificationScheduler();
    await bootAndSignIn(tester, scheduler: scheduler);

    await tester.tap(find.text('Settings').last);
    await tester.pumpAndSettle();

    final toggle = find.text('Daily reminders');
    for (var attempt = 0; attempt < 12 && toggle.evaluate().isEmpty; attempt++) {
      await tester.drag(find.byType(ListView).first, const Offset(0, -400));
      await tester.pumpAndSettle();
    }

    expect(toggle, findsOneWidget,
        reason: 'the only thing the app does when it is closed should have a '
            'switch where a learner looks to stop it');
  });
}
