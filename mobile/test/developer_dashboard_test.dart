import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/api/wordos_api.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/mock_backend/engine/mock_engine.dart';
import 'package:wordos/mock_backend/mock_wordos_api.dart';

import 'support/test_harness.dart';

/// Authorization is the point of these tests. Hiding a nav item is not access
/// control — a normal account must be refused **by the API**, whatever the
/// client renders.
void main() {
  MockWordOsApi apiFor(String email, String password) {
    final engine = MockEngine();
    String? token;
    final api = MockWordOsApi(engine: engine, tokenReader: () => token);
    token = engine.login(email, password).token;
    return api;
  }

  group('authorization', () {
    test('a normal learner is refused every admin endpoint', () async {
      final api = apiFor('demo@wordos.app', 'wordos123');

      for (final call in <Future<Object?> Function()>[
        api.adminOverview,
        api.adminUsers,
        () => api.adminUserDetail('u_1'),
      ]) {
        await expectLater(
          call(),
          throwsA(isA<ApiException>()
              .having((e) => e.code, 'code', 'FORBIDDEN')
              .having((e) => e.statusCode, 'status', 403)),
        );
      }
    });

    test('the owner is allowed', () async {
      final api = apiFor('owner@wordos.app', 'wordos123');

      final overview = await api.adminOverview();
      final users = await api.adminUsers();

      expect(overview.userCount, greaterThan(0));
      expect(users.items, isNotEmpty);
    });

    test('registering never yields an owner account', () async {
      final engine = MockEngine();
      final auth = engine.register('new@wordos.app', 'wordos123', 'New',
            phoneCountryCode: '967', phoneNumber: '770000010');

      expect(auth.user.role, UserRole.user);
    });

    test('an unknown user id is a 404, not an empty dashboard', () async {
      final api = apiFor('owner@wordos.app', 'wordos123');

      await expectLater(
        api.adminUserDetail('does_not_exist'),
        throwsA(isA<ApiException>()
            .having((e) => e.statusCode, 'status', 404)),
      );
    });
  });

  group('overview reflects real data', () {
    test('counts learners, not the owner', () async {
      final api = apiFor('owner@wordos.app', 'wordos123');
      final overview = await api.adminOverview();
      final users = await api.adminUsers();

      final owners = users.items.where((u) => u.role == UserRole.owner).length;
      expect(owners, 1);
      expect(overview.userCount, users.items.length - owners);
    });

    test('spelling appears in skill stats but not in level distributions', () async {
      final api = apiFor('owner@wordos.app', 'wordos123');
      final overview = await api.adminOverview();

      expect(
        overview.skillStats.map((s) => s.skill),
        contains(SkillType.spelling),
      );
      expect(
        overview.levelDistributions.map((d) => d.skill),
        isNot(contains(SkillType.spelling)),
        reason: 'spelling carries no CEFR band (ADR-008)',
      );
    });

    test('custom interests are flagged so the catalogue can grow', () async {
      final api = apiFor('owner@wordos.app', 'wordos123');
      final overview = await api.adminOverview();

      final custom = overview.topInterests.where((i) => i.isCustom);
      expect(custom, isNotEmpty,
          reason: 'the seeded learner has a typed interest');
      expect(
        overview.topInterests.firstWhere((i) => i.interest == 'technology').isCustom,
        isFalse,
      );
    });

    test('failure distribution shares sum to 1 when failures exist', () async {
      final api = apiFor('owner@wordos.app', 'wordos123');
      final overview = await api.adminOverview();

      final total = overview.failureDistribution.values.fold(0.0, (a, b) => a + b);
      expect(total, closeTo(1.0, 1e-9));
    });
  });

  group('user drill-down', () {
    test('reports the learner\'s journey, mistakes and daily activity',
        () async {
      final api = apiFor('owner@wordos.app', 'wordos123');
      final users = await api.adminUsers();
      final demo = users.items.firstWhere((u) => u.email == 'demo@wordos.app');

      final detail = await api.adminUserDetail(demo.id);

      expect(detail.summary.email, 'demo@wordos.app');
      expect(detail.interests, contains('technology'));
      expect(detail.levels.length, 5);
      expect(detail.wordsActive, greaterThan(0));
      expect(detail.masteredWords, isNotEmpty);
      expect(detail.daily, isNotEmpty);
      expect(detail.signInCount, greaterThan(0));

      // The seeded learner failed Listening on one word.
      expect(detail.mistakes, isNotEmpty);
      expect(detail.mistakes.first.skill, SkillType.listening);
    });

    test('a wrong answer is recorded rather than removing the word', () async {
      final api = apiFor('owner@wordos.app', 'wordos123');
      final users = await api.adminUsers();
      final demo = users.items.firstWhere((u) => u.email == 'demo@wordos.app');
      final detail = await api.adminUserDetail(demo.id);

      final failed = detail.mistakes.first;
      expect(detail.wordsLearning, greaterThan(0));
      expect(failed.attempts, greaterThan(0));
      expect(failed.text, isNotEmpty,
          reason: 'the word still exists after being failed (rule R8)');
    });
  });

  group('the time skip (ADR-037)', () {
    test('a normal learner is refused, whoever the schedule belongs to',
        () async {
      final api = apiFor('demo@wordos.app', 'wordos123');
      final me = await api.me();

      // Their own schedule, even. Moving time is an Owner's tool, and hiding
      // the button is not access control.
      await expectLater(
        api.adminAdvanceSchedule(me.id),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'FORBIDDEN')
            .having((e) => e.statusCode, 'status', 403)),
      );
    });

    test('the owner brings a waiting skill forward without waiting', () async {
      final engine = MockEngine();
      String? token;
      final api = MockWordOsApi(engine: engine, tokenReader: () => token);

      // A learner with a word part-way through the pipeline.
      token = engine.login('demo@wordos.app', 'wordos123').token;
      final learner = await api.me();

      final before = await api.words();
      expect(before.items, isNotEmpty);

      token = engine.login('owner@wordos.app', 'wordos123').token;
      final result = await api.adminAdvanceSchedule(learner.id, days: 2);

      expect(result.days, 2);
      expect(result.wordsShifted, before.total);
      // Two days is exactly the gap, so whatever was waiting is now due.
      expect(result.skillsDueNow, greaterThan(0));
    });

    test('a nonsense number of days is clamped, not obeyed', () async {
      final engine = MockEngine();
      String? token;
      final api = MockWordOsApi(engine: engine, tokenReader: () => token);

      token = engine.login('demo@wordos.app', 'wordos123').token;
      final learner = await api.me();
      token = engine.login('owner@wordos.app', 'wordos123').token;

      for (final days in [0, -30, 999999999]) {
        final result = await api.adminAdvanceSchedule(learner.id, days: days);
        expect(result.days, inInclusiveRange(1, 30));
      }
    });
  });

  group('routing and rendering', () {
    Future<void> openSettings(WidgetTester tester) async {
      await tester.tap(find.byIcon(Icons.settings_outlined));
      await tester.pumpAndSettle();
      await tester.pump(const Duration(seconds: 1));
      await tester.pumpAndSettle();
    }

    testWidgets('a normal learner never sees the developer entry point',
        (tester) async {
      await bootAndSignIn(tester);
      await openSettings(tester);

      expect(find.text('Developer Dashboard'), findsNothing);
      expect(find.text('Skip 2 days'), findsNothing);
    });

    testWidgets('a hand-typed reporting window cannot take the dashboard down',
        (tester) async {
      // The reported crash: the presets worked and typing a range closed the
      // app. The number reaches date arithmetic that throws outside a range
      // nobody bothered to check, so every kind of nonsense is typed here.
      await bootApp(tester, surfaceSize: const Size(1200, 2600));
      await tester.pumpAndSettle();

      await tester.enterText(
          find.byType(TextFormField).first, 'owner@wordos.app');
      await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
      await tester.pumpAndSettle();

      await openSettings(tester);
      await tester.scrollUntilVisible(find.text('Developer Dashboard'), 250);
      await tester.tap(find.text('Developer Dashboard'));
      await tester.pumpAndSettle();
      await tester.pump(const Duration(seconds: 1));
      await tester.pumpAndSettle();

      for (final typed in [
        '3',            // the ordinary case
        '0',            // no days at all
        '-7',           // backwards
        '999999999',    // the overflow
        '2147483647',   // the largest int32
        '99999999999999999999', // larger than an int
        'abc',          // not a number
        '3.5',          // not a whole one
        '',             // nothing
        '   ',
      ]) {
        final custom = find.byType(ActionChip);
        await tester.ensureVisible(custom.first);
        await tester.tap(custom.first);
        await tester.pumpAndSettle();

        final field = find.descendant(
          of: find.byType(AlertDialog),
          matching: find.byType(TextField),
        );
        expect(field, findsOneWidget,
            reason: 'the custom range dialog should be open');

        await tester.enterText(field, typed);
        await tester.pumpAndSettle();

        final save = find.descendant(
          of: find.byType(AlertDialog),
          matching: find.widgetWithText(FilledButton, 'Save'),
        );

        // Save is disabled when the field holds nothing usable, so the way out
        // of the dialog is Cancel — which must also leave the screen standing.
        final enabled = tester.widget<FilledButton>(save).onPressed != null;
        await tester.tap(enabled
            ? save
            : find.descendant(
                of: find.byType(AlertDialog),
                matching: find.widgetWithText(TextButton, 'Cancel'),
              ));
        await tester.pumpAndSettle();
        await tester.pump(const Duration(seconds: 1));
        await tester.pumpAndSettle();

        expect(tester.takeException(), isNull,
            reason: 'typing "$typed" as a reporting window crashed the dashboard');

        // Still a dashboard, not a blank screen or an error page.
        expect(find.text('Learners'), findsOneWidget,
            reason: 'the overview disappeared after typing "$typed"');
      }
    });

    testWidgets('a learner writes to the owner and the owner reads it',
        (tester) async {
      // The whole point of the feature end to end: before this a learner who
      // hit a problem had no way to tell anyone (ADR-053).
      await bootApp(tester, surfaceSize: const Size(1200, 2600));
      await tester.pumpAndSettle();

      await tester.enterText(
          find.byType(TextFormField).first, 'demo@wordos.app');
      await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
      await tester.pumpAndSettle();

      await openSettings(tester);
      await tester.scrollUntilVisible(find.text('Message the team'), 250);
      await tester.tap(find.text('Message the team'));
      await tester.pumpAndSettle();

      await tester.enterText(
          find.byType(TextField).last, 'The microphone button does nothing.');
      await tester.tap(find.widgetWithText(FilledButton, 'Send'));
      await tester.pumpAndSettle();

      // Confirmed to the learner, so they know it went somewhere.
      expect(find.text('Sent — thank you.'), findsOneWidget);

      // Now the Owner, who has never seen it.
      await tester.pumpAndSettle(const Duration(seconds: 4));

      await tester.tap(find.descendant(
        of: find.byType(AppBar),
        matching: find.byIcon(Icons.logout_rounded),
      ));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Sign out'));
      await tester.pumpAndSettle();
      await tester.pump(const Duration(seconds: 2));
      await tester.pumpAndSettle();

      await tester.enterText(
          find.byType(TextFormField).first, 'owner@wordos.app');
      await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
      await tester.pumpAndSettle();

      await openSettings(tester);
      await tester.scrollUntilVisible(find.text('Developer Dashboard'), 250);
      await tester.tap(find.text('Developer Dashboard'));
      await tester.pumpAndSettle();
      await tester.pump(const Duration(seconds: 1));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Feedback').last);
      await tester.pumpAndSettle();
      await tester.pump(const Duration(seconds: 1));
      await tester.pumpAndSettle();

      // Their words, and a way to answer them.
      expect(find.text('The microphone button does nothing.'), findsOneWidget);
      expect(find.text('demo@wordos.app'), findsWidgets);

      // And it can be marked dealt with, then put back.
      await tester.tap(find.text('Mark handled').first);
      await tester.pumpAndSettle();
      await tester.pump(const Duration(seconds: 1));
      await tester.pumpAndSettle();

      expect(find.text('Mark unread'), findsWidgets);
    });

    testWidgets('the numbers on screen say what they are', (tester) async {
      // Every figure the dashboard draws is a count of something, and the
      // screen has to say which something. "24 sessions · 11 passed · 4 failed"
      // invites the reader to add the last two and get the first, which is not
      // a sum that exists: a session covers several words (ADR-052).
      await bootApp(tester, surfaceSize: const Size(1200, 2600));
      await tester.pumpAndSettle();

      await tester.enterText(
          find.byType(TextFormField).first, 'owner@wordos.app');
      await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
      await tester.pumpAndSettle();

      await openSettings(tester);
      await tester.scrollUntilVisible(find.text('Developer Dashboard'), 250);
      await tester.tap(find.text('Developer Dashboard'));
      await tester.pumpAndSettle();
      await tester.pump(const Duration(seconds: 1));
      await tester.pumpAndSettle();

      // The overview tile that used to print the mean in raw seconds — "avg
      // 2716s" for a population whose middle session is sixteen seconds long.
      expect(
        find.byWidgetPredicate((w) =>
            w is Text && (w.data ?? '').startsWith('typical ')),
        findsOneWidget,
        reason: 'the session tile should report the typical session',
      );

      // The list is of accounts, because it contains the Owner's own. Calling
      // it learners made it disagree with the overview about the same word.
      await tester.tap(find.text('Users').last);
      await tester.pumpAndSettle();
      await tester.pump(const Duration(seconds: 1));
      await tester.pumpAndSettle();

      expect(
        find.byWidgetPredicate((w) =>
            w is Text && (w.data ?? '').contains(' accounts')),
        findsWidgets,
        reason: 'the list counts accounts, not learners',
      );

      // A learner row reports the sessions it draws. The server never sent
      // this field, so every row read "0 sessions" however much they had done.
      final rows = find.byWidgetPredicate((w) =>
          w is Text && (w.data ?? '').contains('sessions done'));
      expect(rows, findsWidgets);

      final drawn = tester
          .widgetList<Text>(rows)
          .map((t) => t.data!)
          .toList();

      expect(
        drawn.any((line) => !line.contains('0 sessions done')),
        isTrue,
        reason: 'a learner with a history should not read as zero: $drawn',
      );

      // And the per-skill line names its units, so the three numbers are not
      // read as one sum.
      await tester.tap(find.text('Demo Learner').first);
      await tester.pumpAndSettle();
      await tester.pump(const Duration(seconds: 1));
      await tester.pumpAndSettle();

      final page = find.byType(ListView).first;
      await tester.scrollUntilVisible(
        find.text('Performance by skill'), 300,
        scrollable: find.descendant(
          of: page,
          matching: find.byType(Scrollable),
        ).first,
      );

      expect(
        find.byWidgetPredicate((w) =>
            w is Text && (w.data ?? '').contains('words:')),
        findsWidgets,
        reason: 'passed and failed are words, and the line should say so',
      );
    });

    testWidgets('the owner reaches the dashboard and its charts render',
        (tester) async {
      await bootApp(tester, surfaceSize: const Size(1200, 2600));
      await tester.pumpAndSettle();

      await tester.enterText(
          find.byType(TextFormField).first, 'owner@wordos.app');
      await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
      await tester.pumpAndSettle();

      expect(find.text('Skills Hub'), findsWidgets,
          reason: 'the owner should be signed in');

      await openSettings(tester);
      await tester.scrollUntilVisible(find.text('Developer Dashboard'), 250);
      expect(find.text('Developer Dashboard'), findsOneWidget);

      await tester.tap(find.text('Developer Dashboard'));
      await tester.pumpAndSettle();
      await tester.pump(const Duration(seconds: 1));
      await tester.pumpAndSettle();

      // Overview tab.
      expect(find.text('Learners'), findsOneWidget);
      expect(find.text('Pass rate by skill'), findsOneWidget);
      // One scroll is enough to prove the lower charts build; scrolling further
      // is ambiguous here because the TabBarView contributes its own
      // Scrollable alongside the list.
      await tester.drag(find.text('Pass rate by skill'), const Offset(0, -900));
      await tester.pumpAndSettle();
      expect(find.text('Where failures land'), findsOneWidget);

      // Users tab → drill-down.
      await tester.tap(find.text('Users').last);
      await tester.pumpAndSettle();
      await tester.pump(const Duration(seconds: 1));
      await tester.pumpAndSettle();

      expect(find.text('Demo Learner'), findsWidgets);
      await tester.tap(find.text('Demo Learner').first);
      await tester.pumpAndSettle();
      await tester.pump(const Duration(seconds: 1));
      await tester.pumpAndSettle();

      expect(find.text('Account'), findsOneWidget);
      expect(find.text('Sign-ins'), findsOneWidget);

      // Scrolled by content rather than by a fixed distance: a section added
      // above these would otherwise make the drag stop short, and the test
      // would report a missing chart when the chart is simply further down.
      // Scrolled by content rather than by a fixed distance: a section added
      // above these would otherwise make the drag stop short, and the test
      // would report a missing chart when the chart is simply further down.
      // `find.byType(ListView).first` — the page has more than one scrollable.
      final page = find.byType(ListView).first;
      for (final section in ['Day by day', 'Words got wrong']) {
        await tester.scrollUntilVisible(find.text(section), 300,
            scrollable: find.descendant(
              of: page,
              matching: find.byType(Scrollable),
            ).first);
        expect(find.text(section), findsOneWidget);
      }
    });
  });
}
