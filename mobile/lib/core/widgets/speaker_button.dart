import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../audio/speech_provider.dart';
import '../l10n/app_strings.dart';
import '../audio/speech_service.dart';
import '../theme/app_tokens.dart';

/// The speaker control, used everywhere English is spoken.
///
/// One widget rather than a play button per screen, because the requirement is
/// behavioural, not visual: first tap plays, second tap stops **immediately**,
/// and the icon always reflects what the speakers are actually doing (§10).
///
/// The state is read from [SpeechService] rather than held locally, so the icon
/// cannot drift out of step — when audio ends on its own, or another speaker
/// button takes over, this one returns to idle without being told.
class SpeakerButton extends ConsumerWidget {
  const SpeakerButton({
    super.key,
    required this.id,
    required this.text,
    this.rate = SpeechRate.normal,
    this.size = 20,
    this.tooltip,
    this.color,
    this.slow = false,
    this.dense = false,
  });

  /// Tighter padding, for a pair that sits inside a line of text (ADR-119).
  final bool dense;

  /// Identifies this utterance. Two buttons speaking different things must use
  /// different ids, or both would light up together.
  final String id;

  final String text;
  final SpeechRate rate;
  final double size;
  final String? tooltip;
  final Color? color;

  /// Draws the slow-speed face and names itself accordingly. Kept separate
  /// from [rate] on purpose: a caller may want the slow voice without the
  /// button announcing it — the placement screen does — and a button that
  /// looked identical to its neighbour would be the real problem.
  final bool slow;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(stringsProvider);
    final speech = ref.watch(speechServiceProvider);
    final playing = speech.isSpeakingId(id);
    final tint = color ?? context.colors.primary;

    return IconButton(
      onPressed: text.trim().isEmpty
          ? null
          : () => speech.toggle(id, text, rate: rate),
      // Named, so the slow one is distinguishable by a screen reader and by
      // anyone who long-presses rather than guessing from a small glyph.
      tooltip: tooltip ?? (slow ? s.slowSpeed : null),
      iconSize: size,
      visualDensity: VisualDensity.compact,
      style: IconButton.styleFrom(
        foregroundColor: playing ? tint : context.colors.onSurface,
        backgroundColor:
            playing ? tint.withValues(alpha: 0.12) : Colors.transparent,
        // Through the style, not IconButton's own padding and constraints:
        // Material 3 pads every icon button to a 48-point tap target, which
        // is what kept a dense pair from fitting beside a question.
        padding: dense ? const EdgeInsets.all(AppSpacing.xxs) : null,
        minimumSize: dense ? Size.square(size + AppSpacing.sm) : null,
        tapTargetSize: dense ? MaterialTapTargetSize.shrinkWrap : null,
      ),
      icon: Icon(
        // A distinct stop icon, not a differently-coloured speaker: the learner
        // needs to know the second tap will stop it, not replay it.
        playing
            ? Icons.stop_rounded
            : (slow
                ? Icons.slow_motion_video_rounded
                : Icons.volume_up_rounded),
      ),
    );
  }
}

/// How a word sounds — at ordinary speed, and slowly.
///
/// The pair, not a single button, and **everywhere a word can be heard**: the
/// slow voice is the one a learner reaches for when they did not catch the
/// word the first time, and it used to exist on exactly two screens. A learner
/// who finds it beside a Listening question and not in their own word list has
/// not learned that the app has two speeds; they have learned that this screen
/// is missing something (ADR-083).
///
/// Two ids, one per speed, so the two buttons can never light up together.
class WordSpeakerButtons extends StatelessWidget {
  const WordSpeakerButtons({
    super.key,
    required this.id,
    required this.text,
    this.size = 20,
    this.color,
    this.dense = false,
  });

  /// See [SpeakerButton.dense].
  final bool dense;

  /// Identifies this word's utterances. The slow one derives from it, so a
  /// caller never has to remember to make them differ.
  final String id;

  final String text;
  final double size;
  final Color? color;

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        SpeakerButton(
            id: id, text: text, size: size, color: color, dense: dense),
        SpeakerButton(
          id: 'slow:$id',
          text: text,
          rate: SpeechRate.slow,
          size: size,
          color: color,
          slow: true,
          dense: dense,
        ),
      ],
    );
  }
}

/// A larger play/stop control for a whole passage, as Listening uses.
///
/// Same service, same guarantees — it just states the action in words, because
/// on the Listening screen the control is the primary action rather than an
/// affordance beside a word.
class SpeechPlayButton extends ConsumerWidget {
  const SpeechPlayButton({
    super.key,
    required this.id,
    required this.text,
    required this.playLabel,
    required this.stopLabel,
    this.rate = SpeechRate.normal,
  });

  final String id;
  final String text;
  final String playLabel;
  final String stopLabel;
  final SpeechRate rate;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final speech = ref.watch(speechServiceProvider);
    final playing = speech.isSpeakingId(id);

    return FilledButton.tonalIcon(
      onPressed: () => speech.toggle(id, text, rate: rate),
      icon: Icon(playing ? Icons.stop_rounded : Icons.play_arrow_rounded),
      label: Text(playing ? stopLabel : playLabel),
    );
  }
}
