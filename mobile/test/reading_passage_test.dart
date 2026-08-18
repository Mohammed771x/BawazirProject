import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/widgets/app_widgets.dart';
import 'package:wordos/features/session/session_widgets.dart';
import 'package:wordos/features/session/word_lookup_sheet.dart';

import 'support/test_harness.dart';

/// Reading the passage itself (Part 2 §14–§20).
///
/// The passage is the one screen a learner actually spends time on, and the
/// requirement is that it behaves like something to read rather than something
/// to get past: any word can be tapped, an unfamiliar one explains itself, and
/// the words the session is about to test do not give their answers away.
void main() {
  Future<void> openReading(WidgetTester tester) async {
    // A passage runs to ~1800 logical pixels; the default test window would put
    // most of it out of reach of a tap. Taller window, same widget tree.
    await bootAndSignIn(tester, surfaceSize: const Size(1200, 7000));
    await tester.tap(find.text('Reading'));
    await tester.pumpAndSettle();
    expect(find.text('Read the passage'), findsOneWidget);
  }

  /// The whole passage, as one string.
  String passageOf(WidgetTester tester) =>
      (tester
              .widget<Text>(find.descendant(
                of: find.byType(HighlightedPassage),
                matching: find.byType(Text),
              ))
              .textSpan! as TextSpan)
          .toPlainText();

  /// The session's target words, read from the pills under the passage.
  List<String> targetsOf(WidgetTester tester) => tester
      .widgetList<StatusPill>(find.byType(StatusPill))
      .map((p) => p.label)
      .toList();

  testWidgets('any ordinary word can be tapped for its meaning',
      (tester) async {
    await openReading(tester);

    // A word the passage contains that the session is *not* about to test.
    // Whole words only: "book" is a substring of "notebook", and tapping the
    // latter would look up a different word entirely.
    // Taken from the opening of the passage, where the generated text is
    // certainly on screen, and required to occur exactly once so the tap has
    // one place to land.
    final targets = targetsOf(tester).map((t) => t.toLowerCase()).toSet();
    final passage = passageOf(tester).toLowerCase();
    final word = RegExp(r'[a-z]{5,}')
        .allMatches(passage.substring(0, 160))
        .map((m) => m.group(0)!)
        .firstWhere((w) =>
            !targets.contains(w) &&
            RegExp('\\b$w\\b').allMatches(passage).length == 1);

    await tester.tapOnText(find.textRange.ofSubstring(word).first);
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 1)); // the lookup round-trip
    await tester.pumpAndSettle();

    // It gets a sheet of its own — the word, a speaker, and whatever the
    // lexicon has to say. What it must never be is the target-word sheet,
    // which withholds the meaning on purpose.
    expect(find.byType(BottomSheet), findsOneWidget, reason: 'tapped "$word"');
    expect(find.textContaining('meaning stays hidden'), findsNothing);
  });

  testWidgets('a known word offers to join the pipeline, once', (tester) async {
    await openReading(tester);

    // Driven directly rather than through a tap, because which words the
    // generated passage happens to contain is not this test's subject: what
    // matters is what the sheet does with a word the dictionary knows (§20).
    final context = tester.element(find.byType(HighlightedPassage));
    unawaited(showWordLookup(context,
        word: 'research', isTarget: false, color: Colors.blue));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 1));
    await tester.pumpAndSettle();

    expect(find.text('Add to my words'), findsWidgets);

    await tester.tap(find.text('Add to my words').first);
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();

    // The learner already owns "research" — they met it in this passage
    // *because* it is one of theirs. That is a fact to state, not an error.
    expect(find.text('Already in your words'), findsOneWidget);
  });

  testWidgets('a target word is pronounced but never explained',
      (tester) async {
    await openReading(tester);

    final target = targetsOf(tester).first;
    expect(passageOf(tester).toLowerCase(), contains(target.toLowerCase()));

    // `.first` because the word is also printed on the pill below the passage;
    // the passage comes first in the tree.
    await tester.tapOnText(find.textRange.ofSubstring(target).first);
    await tester.pumpAndSettle();

    // Its meaning is exactly what the next questions ask for, so the sheet
    // says so rather than answering them.
    expect(
      find.textContaining('meaning stays hidden'),
      findsOneWidget,
      reason: 'a target word must not be defined mid-session (§19). '
          'Tapped "$target"',
    );
    expect(find.text('Add to my words'), findsNothing);
  });

  testWidgets('the passage is set for reading, not for glancing at',
      (tester) async {
    await openReading(tester);

    final span = tester
        .widget<Text>(find.descendant(
          of: find.byType(HighlightedPassage),
          matching: find.byType(Text),
        ))
        .textSpan! as TextSpan;
    final style = span.children!.whereType<TextSpan>().first.style!;

    // §14–§16: a passage is read for a minute at a time. The exact numbers are
    // a design choice, but "bigger than body text, with room between lines" is
    // the requirement and is worth pinning.
    expect(style.fontSize, greaterThanOrEqualTo(18));
    expect(style.height, greaterThanOrEqualTo(1.6));
  });
}
