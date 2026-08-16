import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:intl/intl.dart' hide TextDirection;

import '../../core/audio/tts_service.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';

/// Renders generated content with the target words underlined/highlighted,
/// exactly as the documents require — visible, but never explained inline.
class HighlightedPassage extends StatelessWidget {
  const HighlightedPassage({
    super.key,
    required this.content,
    required this.color,
  });

  final SessionContent content;
  final Color color;

  @override
  Widget build(BuildContext context) {
    final spans = [...content.targetSpans]
      ..sort((a, b) => a.start.compareTo(b.start));
    final base = context.text.bodyLarge?.copyWith(height: 1.7);
    final pieces = <TextSpan>[];
    var cursor = 0;

    for (final span in spans) {
      if (span.start > content.text.length) break;
      final end = span.end.clamp(0, content.text.length);
      if (span.start > cursor) {
        pieces.add(
          TextSpan(text: content.text.substring(cursor, span.start), style: base),
        );
      }
      pieces.add(
        TextSpan(
          text: content.text.substring(span.start, end),
          style: base?.copyWith(
            color: color,
            fontWeight: FontWeight.w700,
            decoration: TextDecoration.underline,
            decorationColor: color.withValues(alpha: 0.5),
            decorationThickness: 2,
          ),
        ),
      );
      cursor = end;
    }
    if (cursor < content.text.length) {
      pieces.add(TextSpan(text: content.text.substring(cursor), style: base));
    }

    return SelectionArea(
      child: RichText(text: TextSpan(children: pieces)),
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
              if (transcript != null) ...[
                const SizedBox(height: AppSpacing.lg),
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
    final ok =
        await ref.read(ttsServiceProvider).speak(widget.text, slow: slow);
    if (mounted && !ok) setState(() => _audioFailed = true);
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
                child: FilledButton.tonalIcon(
                  onPressed: () => _speak(),
                  icon: Icon(_played
                      ? Icons.replay_rounded
                      : Icons.play_arrow_rounded),
                  label: Text(_played ? s.playAgain : s.playAudio),
                ),
              ),
              const SizedBox(width: AppSpacing.xs),
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: () => _speak(slow: true),
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
                  Text(widget.text, style: context.text.bodyMedium),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }
}
