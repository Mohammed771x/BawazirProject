import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/core/storage/token_store.dart';
import 'package:wordos/core/widgets/app_widgets.dart';

/// Shared driving for the integration journeys.
///
/// Everything here taps real widgets and waits on real HTTP. The only things
/// substituted are the two device-owned settings — the locale, so assertions
/// can be written in English, and the keystore, which is unavailable under
/// `flutter test`. The API, the tokens, the backend and Gemini are all real.

const testPassword = 'correct-horse-battery';

int _bootCount = 0;

/// Boots the app. Passing the same [store] across two calls makes the second a
/// relaunch rather than a new install.
Future<void> boot(WidgetTester tester, {TokenStore? store}) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        appPreferencesProvider.overrideWithValue(
            InMemoryAppPreferences(locale: const Locale('en'))),
        tokenStoreProvider.overrideWith((ref) => store ?? MemoryTokenStore()),
      ],
      // A fresh key each boot: without it Flutter reuses the existing element
      // tree for the same `const WordOsApp()` and a "relaunch" proves nothing.
      child: KeyedSubtree(
        key: ValueKey('boot-${_bootCount++}'),
        child: const WordOsApp(),
      ),
    ),
  );
  await settle(tester);
}

/// Registers a new learner and completes onboarding, landing on the Hub.
Future<void> bootFreshLearner(
  WidgetTester tester, {
  required String prefix,
  TokenStore? store,
}) async {
  await boot(tester, store: store);

  await tapAny(tester, ['Create account']);
  await settle(tester);

  // By position: the label lives in the field's decoration, and the form is
  // name → email → password.
  final fields = find.byType(TextFormField);
  await tester.enterText(fields.at(0), 'Layla');
  await tester.enterText(
      fields.at(1), '$prefix-${DateTime.now().millisecondsSinceEpoch}@wordos.test');
  await tester.enterText(fields.at(2), testPassword);
  await tester.pump(const Duration(milliseconds: 100));

  await tapAny(tester, ['Create account']);
  await settle(tester);

  // The chips read "💻  Technology", so the match is on a substring.
  for (final interest in ['Technology', 'Science', 'Travel']) {
    final chip = find.textContaining(interest);
    if (chip.evaluate().isNotEmpty) {
      await tester.tap(chip.first, warnIfMissed: false);
      await tester.pump(const Duration(milliseconds: 150));
    }
  }
  await tapAny(tester, ['Continue']);
  await settle(tester);

  await completePlacement(tester);

  expect(isOnHub(tester), isTrue,
      reason: 'onboarding should end on the Skills Hub. '
          'Visible: ${visibleText(tester)}');
}

/// Answers the adaptive placement test — a precondition, not the subject.
Future<void> completePlacement(WidgetTester tester) async {
  await tapAny(tester, ['Start', 'Start test', 'Begin']);
  await settle(tester);

  for (var i = 0; i < 40; i++) {
    if (find.text('Start learning').evaluate().isNotEmpty) break;

    final options = find.byType(OptionTile);
    if (options.evaluate().isNotEmpty) {
      // A long passage pushes options below the fold, and a tap outside the
      // viewport does nothing at all.
      await tester.ensureVisible(options.first);
      await tester.pump(const Duration(milliseconds: 100));
      await tester.tap(options.first);
    } else if (find.byType(TextField).evaluate().isNotEmpty) {
      // Tapped before typing: the screen clears the controller between
      // questions, which drops the test input connection, and typing into a
      // stale one silently emits no `onChanged`.
      await typeInto(tester, TextField,
          'Last week I studied for about $i hours and read a long article.');
    }
    await tester.pump(const Duration(milliseconds: 150));
    if (!await tapAnyIfPresent(tester, ['Next'])) break;
    await settle(tester);
  }

  await tapAny(tester, ['Start learning']);
  await settle(tester);
}

/// Adds a word from the real lexicon by typing it and picking a sense.
///
/// Returns the Arabic meaning of the sense that was chosen. The learner knows
/// it — they just picked it — so a journey may legitimately use it to answer
/// the target-word question, which is the item that decides whether the word
/// passes the skill.
Future<String> addWord(WidgetTester tester, String word) async {
  await tapAny(tester, ['Add word', 'Add a word', 'Add']);
  await settle(tester);

  await typeInto(tester, TextField, word);

  // Lookup is debounced, so there is a window with no spinner and no results —
  // waiting on the spinner alone returns while the field is still idle.
  final candidates =
      find.byWidgetPredicate((w) => w is AppCard && w.onTap != null);
  await waitFor(tester, () => candidates.evaluate().isNotEmpty);

  // One card per sense: the learner picks the meaning, and that choice is the
  // word's identity (ADR-012).
  expect(candidates, findsWidgets,
      reason: 'the lexicon should offer senses for "$word". '
          'Visible: ${visibleText(tester)}');
  expect(visibleText(tester).any(hasArabic), isTrue,
      reason: 'candidates carry the Arabic meaning of that sense');

  // Picking a sense *is* the add — there is no separate confirm button.
  await tester.ensureVisible(candidates.first);
  await tester.tap(candidates.first);
  await settle(tester);

  // The confirmation names the word and the skill it now waits on — server
  // state, rendered rather than inferred.
  await waitFor(tester, () => visibleText(tester).any((t) => t.trim() == word));
  expect(visibleText(tester).any((t) => t.trim() == word), isTrue,
      reason: 'adding should confirm the word. Visible: ${visibleText(tester)}');

  // The Arabic line on the confirmation is this sense's meaning.
  final meaning = visibleText(tester).firstWhere(hasArabic, orElse: () => '');
  expect(meaning, isNotEmpty, reason: 'the confirmation should show the meaning');

  // "Done", not "Add word": on this screen "Add word" means *add another*, and
  // it happens to be the same string as the screen title — tapping it here
  // silently throws the confirmation away.
  await tapAny(tester, ['Done']);
  await settle(tester);
  await backToHub(tester);
  return meaning;
}

/// Opens a skill from the Hub and waits out the AI call.
Future<void> openSkill(WidgetTester tester, String skill) async {
  expect(isOnHub(tester), isTrue,
      reason: 'expected the Hub before opening $skill. '
          'Visible: ${visibleText(tester)}');
  await tapAny(tester, [skill]);
  await settle(tester, total: const Duration(seconds: 120));
}

/// Answers every remaining question.
///
/// The answer key is never sent to the client, so comprehension questions are
/// answered blind — they measure whether the content level was right, and do
/// not decide any word's fate. The **target-word** question does, and there the
/// learner genuinely knows the answer: [meaning] is the sense they chose when
/// they added the word. Picking it is what a learner who has understood the
/// passage would do.
Future<void> answerEveryQuestion(
  WidgetTester tester, {
  String? meaning,
}) async {
  for (var i = 0; i < 60; i++) {
    if (find.text('Session complete').evaluate().isNotEmpty) return;

    final known = meaning == null
        ? const <Element>[]
        : find
            .byWidgetPredicate((w) => w is OptionTile && w.label == meaning)
            .evaluate();

    final options = find.byType(OptionTile);
    if (known.isNotEmpty) {
      final target = find.byWidgetPredicate(
          (w) => w is OptionTile && w.label == meaning);
      await tester.ensureVisible(target.first);
      await tester.tap(target.first);
      await tester.pump(const Duration(milliseconds: 150));
    } else if (options.evaluate().isNotEmpty) {
      await tester.ensureVisible(options.first);
      await tester.tap(options.first);
      await tester.pump(const Duration(milliseconds: 150));
    }
    if (!await tapAnyIfPresent(tester, ['Check', 'Next', 'Continue', 'Finish'])) {
      return;
    }
    await settle(tester, total: const Duration(seconds: 60));
  }
}

/// Asserts the session ended and the server reported an outcome for the word.
///
/// It does **not** assert a pass: the test answers blind, so the honest
/// expectation is that the word either advances or is scheduled again — never
/// that it disappears (§48).
Future<void> expectPass(WidgetTester tester, String skill) async {
  final result = visibleText(tester).join(' | ');
  expect(result, contains('Session complete'),
      reason: '$skill should end on a result. Visible: $result');
  debugPrint('  $skill result: '
      '${result.substring(0, result.length.clamp(0, 140))}');
}

/// Keeps the conversation going until the **server** ends it.
///
/// There is no finish button by design: the session completes when the AI marks
/// a turn final, which happens once every target word has been used. So the
/// only way to finish Speaking is to keep talking.
Future<void> finishSpeaking(WidgetTester tester, List<String> lines) async {
  for (final line in lines) {
    // The server may end the conversation on any turn, and the completion is
    // in flight for a moment before the result screen appears. Waiting for one
    // or the other avoids sending a turn into a session that is already over.
    await waitFor(
        tester,
        () =>
            find.text('Session complete').evaluate().isNotEmpty ||
            find.byType(TextField).evaluate().isNotEmpty ||
            find.text('Type instead').evaluate().isNotEmpty,
        total: const Duration(seconds: 60));

    if (find.text('Session complete').evaluate().isNotEmpty) return;
    await sendChatTurn(tester, line);
  }

  await waitFor(tester, () => find.text('Session complete').evaluate().isNotEmpty,
      total: const Duration(seconds: 90));

  expect(find.text('Session complete'), findsWidgets,
      reason: 'the conversation should have ended after those turns. '
          'Visible: ${visibleText(tester).take(8)}');
}

Future<void> sendChatTurn(WidgetTester tester, String text) async {
  // Speaking is voice-first: there is no text field until the learner asks for
  // one. A simulator has no working microphone, so these journeys take the same
  // route such a device offers a real learner — "Type instead".
  // The panel takes a moment to settle into a phase: the tutor's line is
  // "spoken" first, and only then does it offer either a microphone or a
  // keyboard. Waiting for one of them avoids racing that transition.
  await waitFor(
      tester,
      () =>
          find.byType(TextField).evaluate().isNotEmpty ||
          find.text('Type instead').evaluate().isNotEmpty,
      total: const Duration(seconds: 30));

  if (find.byType(TextField).evaluate().isEmpty) {
    await tapAny(tester, ['Type instead']);
    await settle(tester);
  }

  await typeInto(tester, TextField, text);

  // Either control works: the panel offers a send button, and the field itself
  // submits on done. Falling back to the field keeps this helper working
  // whichever phase the panel happens to be in.
  final send = find.byIcon(Icons.send_rounded);
  if (send.evaluate().isNotEmpty) {
    await tester.tap(send.first);
  } else {
    await tester.testTextInput.receiveAction(TextInputAction.done);
  }
  await settle(tester, total: const Duration(seconds: 90));
}

/// Every letter tile that has not been used yet.
Finder liveLetterTiles(WidgetTester tester) => find.byWidgetPredicate((w) =>
    w is InkWell &&
    w.onTap != null &&
    w.child is Container &&
    ((w.child! as Container).child is Text));

/// Returns to the Hub and relaunches the app, carrying only the token store.
///
/// A fresh widget tree and a fresh provider scope: every screen re-fetches over
/// HTTP, so anything that survives came from the server.
Future<void> relaunch(WidgetTester tester, TokenStore store) async {
  await backToHub(tester);
  await boot(tester, store: store);
  expect(isOnHub(tester), isTrue,
      reason: 'the stored token should sign the learner straight back in. '
          'Visible: ${visibleText(tester)}');
}

/// Taps the letter tiles that spell [word], in order.
///
/// Tiles are identified by their letter and consumed as they are used — a used
/// tile keeps its place at 30% opacity but stops responding, so each tap has to
/// find one that is still live rather than the first tile bearing that letter.
Future<void> spellWithTiles(WidgetTester tester, String word) async {
  for (final letter in word.split('')) {
    final live = find.byWidgetPredicate((w) =>
        w is InkWell &&
        w.onTap != null &&
        w.child is Container &&
        ((w.child! as Container).child as Text?)?.data == letter);

    expect(live, findsWidgets,
        reason: 'no unused tile left for "$letter" while spelling "$word"');

    await tester.ensureVisible(live.first);
    await tester.tap(live.first);
    await tester.pump(const Duration(milliseconds: 150));
  }
}

/// Spelling is measured but carries no CEFR band (ADR-008) — asserted against
/// what Settings actually renders.
Future<void> expectSpellingIsUnlevelled(WidgetTester tester) async {
  final texts = visibleText(tester);
  final spellingIndex = texts.indexWhere((t) => t.contains('Spelling'));

  expect(spellingIndex, greaterThanOrEqualTo(0),
      reason: 'Settings should list Spelling. Visible: ${texts.take(20)}');

  // The two entries after the skill name are its level and target; a CEFR band
  // must not be among them.
  final nearby = texts
      .skip(spellingIndex)
      .take(3)
      .where((t) => RegExp(r'^(A1|A2|B1|B2|C1|C2)$').hasMatch(t.trim()));

  expect(nearby, isEmpty,
      reason: 'Spelling must not display a CEFR level. '
          'Around it: ${texts.skip(spellingIndex).take(4)}');
  debugPrint('✓ SPELLING · unlevelled in Settings');
}

// ── Primitives ───────────────────────────────────────────────────────────────

/// `pumpAndSettle` gives up on a screen with a running animation, and a Gemini
/// call takes seconds — so time is advanced in slices instead.
Future<void> settle(
  WidgetTester tester, {
  Duration total = const Duration(seconds: 45),
}) async {
  final deadline = DateTime.now().add(total);
  while (DateTime.now().isBefore(deadline)) {
    await tester.pump(const Duration(milliseconds: 250));
    if (find.byType(CircularProgressIndicator).evaluate().isEmpty) {
      await tester.pump(const Duration(milliseconds: 250));
      return;
    }
  }
}

/// Pumps until [condition] holds, or gives up.
///
/// Needed wherever the wait is not a spinner: a debounced lookup, or a screen
/// that swaps content without a loading state.
Future<bool> waitFor(
  WidgetTester tester,
  bool Function() condition, {
  Duration total = const Duration(seconds: 30),
}) async {
  final deadline = DateTime.now().add(total);
  while (DateTime.now().isBefore(deadline)) {
    await tester.pump(const Duration(milliseconds: 200));
    if (condition()) return true;
  }
  return false;
}

/// Every string on screen, `RichText` included — the reading passage highlights
/// its target words with spans, so it is a `RichText` and invisible to a scan
/// that only looks at `Text`.
List<String> visibleText(WidgetTester tester) => [
      ...tester
          .widgetList<Text>(find.byType(Text))
          .map((t) => t.data ?? t.textSpan?.toPlainText() ?? ''),
      ...tester
          .widgetList<RichText>(find.byType(RichText))
          .map((t) => t.text.toPlainText()),
    ].where((s) => s.trim().isNotEmpty).toList();

String longestText(WidgetTester tester) => visibleText(tester)
    .fold('', (longest, t) => t.length > longest.length ? t : longest);

bool isOnHub(WidgetTester tester) =>
    find.text('Reading').evaluate().isNotEmpty &&
    find.text('Listening').evaluate().isNotEmpty;

/// Arabic on screen means the meaning came from the server's lexicon.
bool hasArabic(String text) => RegExp(r'[؀-ۿ]').hasMatch(text);

Future<void> backToHub(WidgetTester tester) async {
  for (var i = 0; i < 5 && !isOnHub(tester); i++) {
    if (await tapAnyIfPresent(
        tester, ['Back to Skills Hub', 'Skills', 'Done', 'Back'])) {
      await settle(tester);
      continue;
    }
    final canPop = tester.any(find.byType(BackButton));
    if (!canPop) break;
    await tester.pageBack();
    await settle(tester);
  }
  expect(isOnHub(tester), isTrue,
      reason: 'expected to be back on the Hub. Visible: ${visibleText(tester)}');
}

Future<void> typeInto(WidgetTester tester, Type fieldType, String text) async {
  final field = find.byType(fieldType);
  expect(field, findsWidgets, reason: 'no $fieldType on screen');
  await tester.ensureVisible(field.first);
  await tester.tap(field.first);
  await tester.pump(const Duration(milliseconds: 150));
  await tester.enterText(field.first, text);
  await tester.pump(const Duration(milliseconds: 150));
}

Future<void> tapAny(WidgetTester tester, List<String> labels) async {
  if (!await tapAnyIfPresent(tester, labels)) {
    fail('none of $labels was on screen. Visible: ${visibleText(tester)}');
  }
}

Future<bool> tapAnyIfPresent(WidgetTester tester, List<String> labels) async {
  for (final label in labels) {
    final found = find.text(label);
    if (found.evaluate().isEmpty) continue;
    await tester.ensureVisible(found.first);
    await tester.tap(found.first, warnIfMissed: false);
    await tester.pump(const Duration(milliseconds: 200));
    return true;
  }
  return false;
}

/// The keystore is unavailable under `flutter test`; tokens live in memory for
/// the run, which is all a journey needs.
class MemoryTokenStore extends TokenStore {
  String? _token;
  String? _refresh;

  @override
  String? get token => _token;

  @override
  String? get refreshToken => _refresh;

  @override
  Future<String?> restore() async => _token;

  @override
  Future<void> save(String token, {String? refreshToken}) async {
    _token = token;
    if (refreshToken != null) _refresh = refreshToken;
  }

  @override
  Future<void> clear() async {
    _token = null;
    _refresh = null;
  }
}
