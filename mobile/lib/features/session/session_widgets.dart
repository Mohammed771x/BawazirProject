import 'dart:async';

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:intl/intl.dart' hide TextDirection;

import '../../core/audio/speech_provider.dart';
import '../../core/audio/speech_service.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';
import 'clip_playback.dart';

/// Renders generated content with the target words underlined/highlighted,
/// exactly as the documents require — visible, but never explained inline.
///
/// Every word is tappable (Part 2 §17–§19). A word the learner does not know
/// should not stop the passage dead: one tap gives the meaning and how it
/// sounds, and the passage carries on. The target words are the exception —
/// those are what the session is about to test, so they are pronounced but not
/// explained. [onWordTap] receives whether the tapped word was a target one,
/// and the screen decides what to show.
///
/// Typography is set here rather than left to the theme's body style (§14–§16):
/// a passage is read for a minute at a time, not glanced at, so it gets a larger
/// size, generous line height and a measured line length.
class HighlightedPassage extends StatefulWidget {
  const HighlightedPassage({
    super.key,
    required this.content,
    required this.color,
    this.onWordTap,
  });

  final SessionContent content;
  final Color color;

  /// Called with the tapped word and whether it is one of the session's target
  /// words. Null makes the passage plain text, as the result screen shows it.
  final void Function(String word, {required bool isTarget})? onWordTap;

  @override
  State<HighlightedPassage> createState() => _HighlightedPassageState();
}

class _HighlightedPassageState extends State<HighlightedPassage> {
  /// Recognisers own native resources; one per word, all released together.
  final _recognizers = <TapGestureRecognizer>[];

  @override
  void dispose() {
    for (final r in _recognizers) {
      r.dispose();
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    for (final r in _recognizers) {
      r.dispose();
    }
    _recognizers.clear();

    final text = widget.content.text;
    final spans = [...widget.content.targetSpans]
      ..sort((a, b) => a.start.compareTo(b.start));

    final base = context.text.bodyLarge?.copyWith(
      fontSize: 19,
      height: 1.85,
      letterSpacing: 0.15,
    );
    // Full strength, not a wash: a 50%-alpha underline on the dark theme's
    // surface is effectively invisible, which loses the one signal telling the
    // learner which words the session is about (§16).
    final targetStyle = base?.copyWith(
      color: widget.color,
      fontWeight: FontWeight.w700,
      decoration: TextDecoration.underline,
      decorationColor: widget.color,
      decorationThickness: 2.5,
    );

    bool isTarget(int start, int end) => spans.any(
        (s) => start < s.end.clamp(0, text.length) && end > s.start);

    final pieces = <InlineSpan>[];
    var cursor = 0;

    // Letters, digits and the apostrophes and hyphens that live inside words —
    // "doesn't" and "well-known" are one word each, not three.
    for (final match in RegExp(r"[\p{L}\p{N}][\p{L}\p{N}'’\-]*", unicode: true)
        .allMatches(text)) {
      if (match.start > cursor) {
        pieces.add(
          TextSpan(text: text.substring(cursor, match.start), style: base),
        );
      }
      cursor = match.end;

      final word = match.group(0)!;
      final target = isTarget(match.start, match.end);

      TapGestureRecognizer? recognizer;
      if (widget.onWordTap != null) {
        recognizer = TapGestureRecognizer()
          ..onTap = () => widget.onWordTap!(word, isTarget: target);
        _recognizers.add(recognizer);
      }

      pieces.add(TextSpan(
        text: word,
        style: target ? targetStyle : base,
        recognizer: recognizer,
      ));
    }
    if (cursor < text.length) {
      pieces.add(TextSpan(text: text.substring(cursor), style: base));
    }

    // The passage is English however the interface is set. Inheriting the
    // app's direction right-aligns every line and starts them from the wrong
    // end — legible, and wrong.
    return Directionality(
      textDirection: TextDirection.ltr,
      child: Text.rich(
        TextSpan(children: pieces),
        textAlign: TextAlign.left,
      ),
    );
  }
}

/// End-of-session summary: what happened to every word and when it returns.
class SessionResultView extends ConsumerWidget {
  const SessionResultView({
    super.key,
    required this.result,
    required this.onClose,
    this.transcript,
  });

  final SessionResult result;
  final VoidCallback onClose;
  final SessionContent? transcript;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final color = SkillVisuals.color(context, result.skill);
    final dateFormat = DateFormat.MMMd(s.locale.languageCode);
    final passed = result.passedCount;

    return Column(
      children: [
        Expanded(
          child: ListView(
            padding: const EdgeInsets.all(AppSpacing.md),
            children: [
              const SizedBox(height: AppSpacing.md),
              Center(
                child: Column(
                  children: [
                    Container(
                      width: 84,
                      height: 84,
                      decoration: BoxDecoration(
                        color: color.withValues(alpha: 0.12),
                        shape: BoxShape.circle,
                      ),
                      child: Icon(SkillVisuals.icon(result.skill),
                          size: 38, color: color),
                    ),
                    const SizedBox(height: AppSpacing.md),
                    Text(s.sessionComplete, style: context.text.headlineSmall),
                    const SizedBox(height: AppSpacing.xxs),
                    Text(
                      '$passed / ${result.words.length}',
                      style: context.text.titleMedium?.copyWith(color: color),
                    ),
                  ],
                ),
              ),
              if (result.comprehensionTotal > 0) ...[
                const SizedBox(height: AppSpacing.lg),
                AppCard(
                  child: Row(
                    children: [
                      Expanded(
                        child: Text(s.comprehension,
                            style: context.text.titleSmall),
                      ),
                      Text(
                        '${result.comprehensionCorrect}/${result.comprehensionTotal}',
                        style: context.text.titleMedium,
                      ),
                    ],
                  ),
                ),
              ],
              // How the conversation went as a whole, before the word-by-word
              // detail. Speaking only.
              if (result.summary != null && result.summary!.trim().isNotEmpty) ...[
                const SizedBox(height: AppSpacing.lg),
                AppCard(
                  color: color.withValues(alpha: 0.07),
                  borderColor: color.withValues(alpha: 0.28),
                  child: Text(result.summary!, style: context.text.bodyMedium),
                ),
              ],
              const SizedBox(height: AppSpacing.lg),
              for (final outcome in result.words) ...[
                _OutcomeTile(outcome: outcome, dateFormat: dateFormat),
                const SizedBox(height: AppSpacing.xs),
              ],
              // Listening ends with the recording back in the learner's hands
              // (§5, §7). During the session the audio was a test; afterwards
              // it is study material — and the one moment they most want to
              // hear it again is having just seen which questions they missed.
              //
              // Order: answers, then the audio, then the text. Hearing it
              // before reading it is the same order as the session itself.
              if (transcript != null) ...[
                const SizedBox(height: AppSpacing.lg),
                if (result.skill == SkillType.listening) ...[
                  SectionHeader(title: s.listenAgain),
                  ReplayPlayer(text: transcript!.text, color: color),
                  const SizedBox(height: AppSpacing.lg),
                ],
                SectionHeader(title: s.showTranscript),
                AppCard(
                  color: context.palette.subtleSurface,
                  child: HighlightedPassage(content: transcript!, color: color),
                ),
              ],
            ],
          ),
        ),
        Padding(
          padding: const EdgeInsets.all(AppSpacing.md),
          child: FilledButton(onPressed: onClose, child: Text(s.backToHub)),
        ),
      ],
    );
  }
}

class _OutcomeTile extends ConsumerWidget {
  const _OutcomeTile({required this.outcome, required this.dateFormat});

  final WordOutcome outcome;
  final DateFormat dateFormat;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final color =
        outcome.passed ? context.palette.success : context.palette.danger;

    return AppCard(
      borderColor: color.withValues(alpha: 0.3),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(
            outcome.passed
                ? Icons.check_circle_rounded
                : Icons.refresh_rounded,
            color: color,
          ),
          const SizedBox(width: AppSpacing.sm),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(outcome.text, style: context.text.titleSmall),
                Text(
                  outcome.meaning,
                  textDirection: TextDirection.rtl,
                  style: context.text.bodySmall?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.65),
                  ),
                ),
                const SizedBox(height: AppSpacing.xs),
                Text(
                  outcome.becameActive
                      ? s.becameActiveMsg(outcome.text)
                      : outcome.passed && outcome.nextSkill != null
                          ? s.nextSkillOn(
                              s.skillName(outcome.nextSkill!),
                              outcome.nextEligibleAt == null
                                  ? '—'
                                  : dateFormat
                                      .format(outcome.nextEligibleAt!.toLocal()),
                            )
                          : s.retryScheduled,
                  style: context.text.bodySmall?.copyWith(color: color),
                ),

                // What the conversation showed about this word — why it went
                // the way it did, and what to say next time. A learner told
                // only that a word failed has learned that they failed and
                // nothing else (ADR-048).
                if (outcome.feedback != null) ...[
                  const SizedBox(height: AppSpacing.sm),
                  Text(outcome.feedback!, style: context.text.bodySmall),
                ],

                // Their own words, quoted back, so the advice has something to
                // point at.
                if (outcome.evidence != null &&
                    outcome.evidence!.trim().isNotEmpty) ...[
                  const SizedBox(height: AppSpacing.xs),
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Icon(Icons.format_quote_rounded,
                          size: 16,
                          color: context.colors.onSurface.withValues(alpha: 0.4)),
                      const SizedBox(width: AppSpacing.xxs),
                      Expanded(
                        child: EnglishText(
                          outcome.evidence!,
                          style: context.text.bodySmall?.copyWith(
                            fontStyle: FontStyle.italic,
                            color:
                                context.colors.onSurface.withValues(alpha: 0.7),
                          ),
                        ),
                      ),
                    ],
                  ),
                ],

                // The sentence to copy. After being told what was wrong, this
                // is the part a learner can actually use tomorrow.
                if (outcome.better != null &&
                    outcome.better!.trim().isNotEmpty) ...[
                  const SizedBox(height: AppSpacing.xs),
                  AppCard(
                    color: context.palette.subtleSurface,
                    child: Row(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Icon(Icons.lightbulb_outline_rounded,
                            size: 16, color: context.palette.warning),
                        const SizedBox(width: AppSpacing.xs),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                s.sayItLikeThis,
                                style: context.text.labelSmall?.copyWith(
                                    color: context.palette.warning),
                              ),
                              const SizedBox(height: AppSpacing.xxs),
                              EnglishText(outcome.better!,
                                  style: context.text.bodySmall),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// The target word inside its neighbouring sentences.
///
/// The learner is asked to infer the meaning from this, so the surrounding
/// sentences are shown at full weight and the target is emphasised rather than
/// isolated — pulling the word out of its context would defeat the exercise
/// (demo review §26–27).
class ContextPassage extends ConsumerWidget {
  const ContextPassage({
    super.key,
    required this.context,
    required this.highlight,
    required this.color,
  });

  final WordContext context;
  final String? highlight;
  final Color color;

  @override
  Widget build(BuildContext buildContext, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final muted =
        buildContext.colors.onSurface.withValues(alpha: 0.55);

    return AppCard(
      color: buildContext.palette.subtleSurface,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // The sentences are English, and are laid out as English whatever
          // the interface language. Inheriting the Arabic direction put each
          // sentence's full stop at its start (".I walk to the market") and
          // right-aligned prose that reads left to right.
          SizedBox(
            width: double.infinity,
            child: Directionality(
              textDirection: TextDirection.ltr,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  if (context.before != null)
                    Text(
                      context.before!,
                      style: buildContext.text.bodyMedium
                          ?.copyWith(color: muted),
                    ),
                  if (context.before != null)
                    const SizedBox(height: AppSpacing.xs),
                  _Sentence(
                    text: context.sentence,
                    highlight: highlight,
                    color: color,
                  ),
                  if (context.after != null) ...[
                    const SizedBox(height: AppSpacing.xs),
                    Text(
                      context.after!,
                      style: buildContext.text.bodyMedium
                          ?.copyWith(color: muted),
                    ),
                  ],
                ],
              ),
            ),
          ),
          const SizedBox(height: AppSpacing.sm),
          Row(
            children: [
              Icon(Icons.psychology_alt_outlined, size: 16, color: muted),
              const SizedBox(width: AppSpacing.xxs),
              Expanded(
                child: Text(
                  s.guessFromContext,
                  style: buildContext.text.labelSmall?.copyWith(color: muted),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

/// Where [word] stands as a word of its own in [text], or -1.
///
/// A whole word first: found by plain search, "deliver" lit up the front of
/// "delivery" earlier in the same sentence, and the word being asked about
/// went unmarked. The first plain match is kept only as a fallback, for a
/// target the sentence spells as part of a longer word and nowhere else.
int wholeWordIndex(String text, String word) {
  if (word.isEmpty) return -1;
  final whole = RegExp(
    '(?<![A-Za-z])${RegExp.escape(word)}(?![A-Za-z])',
    caseSensitive: false,
  ).firstMatch(text);
  if (whole != null) return whole.start;
  return text.toLowerCase().indexOf(word.toLowerCase());
}

class _Sentence extends StatelessWidget {
  const _Sentence({
    required this.text,
    required this.highlight,
    required this.color,
  });

  final String text;
  final String? highlight;
  final Color color;

  @override
  Widget build(BuildContext context) {
    final base = context.text.bodyLarge;
    final target = highlight;
    if (target == null || target.isEmpty) {
      return Text(text, style: base);
    }

    final index = wholeWordIndex(text, target);
    if (index < 0) return Text(text, style: base);

    return RichText(
      text: TextSpan(
        style: base,
        children: [
          TextSpan(text: text.substring(0, index)),
          TextSpan(
            text: text.substring(index, index + target.length),
            style: base?.copyWith(
              color: color,
              fontWeight: FontWeight.w700,
              backgroundColor: color.withValues(alpha: 0.12),
            ),
          ),
          TextSpan(text: text.substring(index + target.length)),
        ],
      ),
    );
  }
}

/// Plays one sentence for a Listening vocabulary item. No transcript is shown:
/// the whole point of the skill is understanding from audio (demo review §33).
/// Play, stop and slow — the recording, handed back after the test.
///
/// Deliberately not autoplaying, unlike the player during the session: nothing
/// should start talking while the learner is reading their result. They press
/// it when they want it.
class ReplayPlayer extends ConsumerStatefulWidget {
  const ReplayPlayer({super.key, required this.text, required this.color});

  final String text;
  final Color color;

  @override
  ConsumerState<ReplayPlayer> createState() => _ReplayPlayerState();
}

class _ReplayPlayerState extends ConsumerState<ReplayPlayer> {
  bool _slow = false;

  String get _id => 'replay:${widget.text.hashCode}';

  Future<void> _toggle() async {
    final speech = ref.read(speechServiceProvider);
    if (speech.isSpeakingId(_id)) {
      await speech.stop();
      return;
    }
    await speech.speak(_id, widget.text,
        rate: _slow ? SpeechRate.slow : SpeechRate.normal);
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    // Read from the service, so the icon cannot claim something is playing
    // when it is not.
    final playing = ref.watch(speechServiceProvider).isSpeakingId(_id);

    return AppCard(
      color: widget.color.withValues(alpha: 0.06),
      borderColor: widget.color.withValues(alpha: 0.28),
      child: Column(
        children: [
          Row(
            children: [
              IconButton.filledTonal(
                onPressed: _toggle,
                iconSize: 28,
                icon: Icon(playing
                    ? Icons.stop_rounded
                    : Icons.play_arrow_rounded),
              ),
              const SizedBox(width: AppSpacing.sm),
              Expanded(
                child: Text(
                  playing ? s.stopAudio : s.listenAgain,
                  style: context.text.titleSmall,
                ),
              ),
            ],
          ),
          const SizedBox(height: AppSpacing.xs),
          SegmentedButton<bool>(
            segments: [
              ButtonSegment(value: false, label: Text(s.normalSpeed)),
              ButtonSegment(value: true, label: Text(s.slowSpeed)),
            ],
            selected: {_slow},
            onSelectionChanged: (value) {
              setState(() => _slow = value.first);
              // Applied at once: a speed control that waits for the next press
              // is a setting, not a control.
              unawaited(_toggle());
            },
          ),
        ],
      ),
    );
  }
}

/// How the target word sounds, beside the question that asks about it.
///
/// Listening is the one skill where a target word is never heard on its own.
/// It arrives buried in a sentence, at speaking speed, once — and then the
/// learner is asked what it means. A learner who did not catch the word is not
/// being tested on meaning at all at that point; they are being tested on
/// whether they heard it, which the comprehension questions already do
/// (ADR-081).
///
/// So the word is offered separately, at both speeds, and only ever the word:
/// the sentence around it stays where it was, because that is the test.
///
/// Nothing here can leak an answer. These items ask what the word *means*, and
/// the options are meanings. It is deliberately not offered on the
/// comprehension questions, which carry no word of their own.
///
/// **It does not show the word.** Listening's whole task is that the learner
/// never sees it: the word arrives as sound, inside a sentence, and they are
/// asked what it meant. Printing it here — as the button's label, or in a
/// tooltip — would hand over the spelling and make the exercise reading with
/// audio attached (ADR-085). The word is still *passed in*, because the device
/// has to be given something to say; it is simply never drawn.
class WordPronunciation extends ConsumerWidget {
  const WordPronunciation({
    super.key,
    required this.word,
    required this.color,
    this.revealSpelling = false,
  });

  final String word;
  final Color color;

  /// Whether the word may be shown as well as heard.
  ///
  /// False everywhere today, and the default, because Listening is the only
  /// caller. It exists so a future use where the word is on screen anyway
  /// states that it is choosing to show it, rather than quietly turning the
  /// rule off (ADR-085). Reading does not use this card: it has the compact
  /// speaker pair beside its question instead (ADR-119).
  final bool revealSpelling;

  /// Distinct ids per speed, so the two controls never light up together.
  String get _normalId => 'pronounce:$word';
  String get _slowId => 'pronounce-slow:$word';

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    // Watched, not read: when an utterance ends by itself these controls have
    // to return to idle without being told.
    final speech = ref.watch(speechServiceProvider);
    final normalPlaying = speech.isSpeakingId(_normalId);
    final slowPlaying = speech.isSpeakingId(_slowId);

    return AppCard(
      color: context.palette.subtleSurface,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(Icons.record_voice_over_rounded, size: 18, color: color),
              const SizedBox(width: AppSpacing.xs),
              Expanded(
                child: Text(s.hearTheWord, style: context.text.labelMedium),
              ),
            ],
          ),
          const SizedBox(height: AppSpacing.sm),
          // Both children are flexed. The theme gives every FilledButton and
          // OutlinedButton `Size.fromHeight(54)` — `Size(double.infinity, 54)`
          // — so an unflexed one inside a Row demands infinite width and fails
          // layout outright. That is what broke the sentence player above.
          Row(
            children: [
              Expanded(
                flex: 2,
                // The tooltip is part of the screen too: a screen reader says
                // it aloud and a long press shows it, so it may not carry the
                // spelling either (ADR-085).
                child: Tooltip(
                  message: revealSpelling
                      ? s.hearTheWordNormally(word)
                      : s.hearTheWord,
                  child: FilledButton.tonalIcon(
                    onPressed: () => unawaited(speech.toggle(_normalId, word)),
                    icon: Icon(
                      normalPlaying
                          ? Icons.stop_rounded
                          : Icons.volume_up_rounded,
                    ),
                    label: revealSpelling
                        // The word is English inside an Arabic interface, so it
                        // is pinned left-to-right. A long one shortens rather
                        // than bursting the button: the label is decoration
                        // here, and the control works when it is clipped.
                        ? EnglishText(
                            word,
                            style: context.text.labelLarge,
                            textAlign: TextAlign.center,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                          )
                        : Text(s.playAudio),
                  ),
                ),
              ),
              const SizedBox(width: AppSpacing.xs),
              Expanded(
                child: Tooltip(
                  message: revealSpelling
                      ? s.hearTheWordSlowly(word)
                      : s.slowSpeed,
                  child: OutlinedButton.icon(
                    onPressed: () => unawaited(
                      speech.toggle(_slowId, word, rate: SpeechRate.slow),
                    ),
                    icon: Icon(
                      slowPlaying
                          ? Icons.stop_rounded
                          : Icons.slow_motion_video_rounded,
                      size: 18,
                    ),
                    label: Text(s.slowSpeed),
                  ),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

/// The sentence a Listening question is about, as its own small player.
///
/// The same control the clip uses, in a card rather than a console: it waits to
/// be started, the face states what the next tap does, and pausing continues
/// from where it stopped rather than beginning again (ADR-080). It is the same
/// engine underneath — `ClipPlayback` — so the two can no longer drift apart,
/// which is exactly how one of them ended up carrying a bug the other did not.
class SentencePlayer extends ConsumerStatefulWidget {
  const SentencePlayer({super.key, required this.text, required this.color});

  final String text;
  final Color color;

  @override
  ConsumerState<SentencePlayer> createState() => _SentencePlayerState();
}

class _SentencePlayerState extends ConsumerState<SentencePlayer> {
  // Not `final`: the next question arrives as a new [text] on this same State
  // and the controller is rebuilt around it — see [didUpdateWidget].
  late ClipPlayback _clip;

  /// Where the finger is while dragging. The bar follows it; the audio does
  /// not move until it is let go.
  double? _scrubbing;

  @override
  void initState() {
    super.initState();
    // No auto-play. This card used to speak the moment the question appeared,
    // on the argument that a silent card looks broken — but the learner is
    // still reading the question, and a voice that starts unasked is one the
    // learner's first act is to silence. The control says what it offers.
    _clip = _newClip();
  }

  ClipPlayback _newClip() => ClipPlayback(
        speech: ref.read(speechServiceProvider),
        text: widget.text,
        idPrefix: 'sentence',
      )..addListener(_repaint);

  @override
  void didUpdateWidget(SentencePlayer oldWidget) {
    super.didUpdateWidget(oldWidget);
    // Moving to the next question does **not** build a new player. Same type,
    // same position in the tree, no key — so Flutter keeps this State and only
    // hands it a new [text]. A controller built once in `initState` therefore
    // outlived the question it was made for: the card showed the new sentence
    // and the voice read the old one, which on a listening test is the
    // previous question's evidence played over this one.
    //
    // Rebuilt here rather than pinned with a `ValueKey` at the call site: a key
    // is a promise every future caller has to remember, and the widget that
    // owns the text is the one that can be sure. Disposing the old controller
    // also silences it, so nothing follows the learner forward.
    if (widget.text != oldWidget.text) {
      _clip.removeListener(_repaint);
      _clip.dispose();
      _scrubbing = null;
      _clip = _newClip();
    }
  }

  void _repaint() {
    if (mounted) setState(() {});
  }

  @override
  void dispose() {
    _clip.removeListener(_repaint);
    // Nothing spoken here may follow the learner to the next question.
    _clip.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    final playing = _clip.isPlaying;

    return AppCard(
      color: context.palette.subtleSurface,
      child: Column(
        children: [
          Row(
            children: [
              Icon(Icons.graphic_eq_rounded, color: widget.color),
              const SizedBox(width: AppSpacing.sm),
              Expanded(
                child: Text(s.listenToSentence,
                    style: context.text.titleSmall),
              ),
              // Back to the first word. An icon in the header rather than a
              // second button under the play one: as a button it had a third
              // of a phone's width and wrapped "Back to the start" onto two
              // cramped lines, and it left the primary control sharing space
              // with something used far less often.
              IconButton(
                onPressed: () => unawaited(_clip.seekTo(0)),
                tooltip: s.jumpToStart,
                iconSize: 20,
                visualDensity: VisualDensity.compact,
                color: widget.color,
                icon: const Icon(Icons.first_page_rounded),
              ),
              // How far in, and how long altogether — the same clock the
              // clip carries, and estimated the same way (ADR-082).
              Text(
                s.clipClock(
                  formatClipTime(_clip.elapsed),
                  formatClipTime(_clip.total),
                ),
                style: context.text.labelSmall?.copyWith(
                  color: context.colors.onSurface.withValues(alpha: 0.7),
                  fontFeatures: const [FontFeature.tabularFigures()],
                ),
              ),
            ],
          ),
          const SizedBox(height: AppSpacing.sm),
          // The full width, because it is the only thing on this card the
          // learner presses often. The theme gives every FilledButton
          // `Size.fromHeight(54)` — `Size(double.infinity, 54)` — so it must
          // either fill the row or be flexed inside one; an unflexed one
          // demands infinite width and fails layout outright.
          FilledButton.tonalIcon(
            onPressed: () => unawaited(_clip.toggle()),
            // The same three faces as the clip, for the same reason: the icon
            // has to state what the *next* tap does, and a replay face on a
            // paused sentence promises the wrong thing.
            icon: Icon(playing
                ? Icons.pause_rounded
                : (_clip.finished
                    ? Icons.replay_rounded
                    : Icons.play_arrow_rounded)),
            // The state, not the next tap — the icon already carries the verb
            // (ADR-082).
            label: Text(playing
                ? s.audioPlaying
                : (_clip.finished
                    ? s.audioFinished
                    : (_clip.started ? s.audioPaused : s.playAudio))),
          ),
          const SizedBox(height: AppSpacing.xs),
          // A track here as well. Without one the learner can pause and
          // continue but cannot go *back* a few words, which is the thing a
          // sentence heard once is most often paused for. The unplayed part is
          // drawn in `trackRest`, strongly enough to show where it ends.
          SliderTheme(
            data: SliderTheme.of(context).copyWith(
              trackHeight: 6,
              activeTrackColor: widget.color,
              inactiveTrackColor: context.palette.trackRest,
              thumbColor: widget.color,
              overlayShape: const RoundSliderOverlayShape(overlayRadius: 14),
            ),
            child: Slider(
              value: (_scrubbing ?? _clip.position)
                  .clamp(0, _clip.totalChars.toDouble()),
              max: _clip.totalChars.toDouble(),
              onChanged: (value) => setState(() => _scrubbing = value),
              onChangeEnd: (value) {
                setState(() => _scrubbing = null);
                unawaited(_clip.seekTo(value));
              },
            ),
          ),
          // Speed is a mode, not a second play button. It used to be one, so
          // "slow" always restarted the sentence and there was no way to be
          // playing slowly and pause. It now changes the voice in place and
          // carries on from the line being spoken.
          SegmentedButton<bool>(
            segments: [
              ButtonSegment(value: false, label: Text(s.normalSpeed)),
              ButtonSegment(value: true, label: Text(s.slowSpeed)),
            ],
            selected: {_clip.slow},
            onSelectionChanged: (value) => _clip.setSlow(value.first),
          ),
          // If the device cannot speak, showing the sentence is a worse
          // listening exercise but a far better outcome than a learner stuck on
          // a question they can never hear (demo review §51).
          if (_clip.audioFailed) ...[
            const SizedBox(height: AppSpacing.sm),
            AppCard(
              color: context.palette.warningSurface,
              borderColor: context.palette.warning.withValues(alpha: 0.35),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Icon(Icons.volume_off_rounded,
                          size: 18, color: context.palette.warning),
                      const SizedBox(width: AppSpacing.xs),
                      Expanded(
                        child: Text(s.audioUnavailable,
                            style: context.text.labelMedium),
                      ),
                    ],
                  ),
                  const SizedBox(height: AppSpacing.xs),
                  // The script is English: it must not inherit the Arabic
                  // interface's direction, exactly as the passage does not.
                  EnglishText(widget.text, style: context.text.bodyMedium),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }
}
