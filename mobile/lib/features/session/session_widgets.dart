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
          if (context.before != null)
            Text(
              context.before!,
              style: buildContext.text.bodyMedium?.copyWith(color: muted),
            ),
          if (context.before != null) const SizedBox(height: AppSpacing.xs),
          _Sentence(
            text: context.sentence,
            highlight: highlight,
            color: color,
          ),
          if (context.after != null) ...[
            const SizedBox(height: AppSpacing.xs),
            Text(
              context.after!,
              style: buildContext.text.bodyMedium?.copyWith(color: muted),
            ),
          ],
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

    final index = text.toLowerCase().indexOf(target.toLowerCase());
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

class SentencePlayer extends ConsumerStatefulWidget {
  const SentencePlayer({super.key, required this.text, required this.color});

  final String text;
  final Color color;

  @override
  ConsumerState<SentencePlayer> createState() => _SentencePlayerState();
}

class _SentencePlayerState extends ConsumerState<SentencePlayer> {
  bool _played = false;
  bool _audioFailed = false;

  @override
  void initState() {
    super.initState();
    // Play once on arrival so the learner is not left looking at a silent card.
    WidgetsBinding.instance.addPostFrameCallback((_) => _speak());
  }

  Future<void> _speak({bool slow = false}) async {
    if (!mounted) return;
    setState(() => _played = true);

    final ok = await ref.read(speechServiceProvider).speak(
          'sentence:${widget.text.hashCode}',
          widget.text,
          rate: slow ? SpeechRate.slow : SpeechRate.normal,
        );
    if (mounted && !ok) setState(() => _audioFailed = true);
  }

  Future<void> _toggle({bool slow = false}) async {
    final speech = ref.read(speechServiceProvider);
    if (speech.isSpeakingId('sentence:${widget.text.hashCode}')) {
      await speech.stop();
      return;
    }
    await _speak(slow: slow);
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
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
            ],
          ),
          const SizedBox(height: AppSpacing.sm),
          // Both buttons are flexed. The theme gives every FilledButton and
          // OutlinedButton `Size.fromHeight(54)` — which is
          // `Size(double.infinity, 54)` — so an unflexed one inside a Row
          // demands infinite width and fails layout outright. That is what
          // broke this player: the replay button was flexed, the slow-speed
          // button next to it was not.
          Row(
            children: [
              Expanded(
                flex: 2,
                child: Builder(builder: (context) {
                  final playing = ref
                      .watch(speechServiceProvider)
                      .isSpeakingId('sentence:${widget.text.hashCode}');

                  return FilledButton.tonalIcon(
                    onPressed: () => _toggle(),
                    icon: Icon(playing
                        ? Icons.stop_rounded
                        : (_played
                            ? Icons.replay_rounded
                            : Icons.play_arrow_rounded)),
                    label: Text(playing
                        ? s.stopAudio
                        : (_played ? s.playAgain : s.playAudio)),
                  );
                }),
              ),
              const SizedBox(width: AppSpacing.xs),
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: () => _toggle(slow: true),
                  icon: const Icon(Icons.slow_motion_video_rounded, size: 18),
                  label: Text(s.slowSpeed),
                ),
              ),
            ],
          ),
          // If the device cannot speak, showing the sentence is a worse
          // listening exercise but a far better outcome than a learner stuck on
          // a question they can never hear (demo review §51).
          if (_audioFailed) ...[
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
