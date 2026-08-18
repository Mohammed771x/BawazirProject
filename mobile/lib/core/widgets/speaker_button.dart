import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../audio/speech_provider.dart';
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
  });

  /// Identifies this utterance. Two buttons speaking different things must use
  /// different ids, or both would light up together.
  final String id;

  final String text;
  final SpeechRate rate;
  final double size;
  final String? tooltip;
  final Color? color;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final speech = ref.watch(speechServiceProvider);
    final playing = speech.isSpeakingId(id);
    final tint = color ?? context.colors.primary;

    return IconButton(
      onPressed: text.trim().isEmpty
          ? null
          : () => speech.toggle(id, text, rate: rate),
      tooltip: tooltip,
      iconSize: size,
      visualDensity: VisualDensity.compact,
      style: IconButton.styleFrom(
        foregroundColor: playing ? tint : context.colors.onSurface,
        backgroundColor:
            playing ? tint.withValues(alpha: 0.12) : Colors.transparent,
      ),
      icon: Icon(
        // A distinct stop icon, not a differently-coloured speaker: the learner
        // needs to know the second tap will stop it, not replay it.
        playing ? Icons.stop_rounded : Icons.volume_up_rounded,
      ),
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
