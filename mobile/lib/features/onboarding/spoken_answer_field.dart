import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/audio/speech_recognition_service.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/app_widgets.dart';

/// A spoken answer, for the Speaking part of the placement test.
///
/// §17 is explicit: Speaking must be *spoken*. Showing a text box here would
/// measure writing and file the result under speaking, which is the single
/// worst thing a placement test can do — it mislabels the evidence.
///
/// So the learner taps once, talks, and stops talking; silence ends the turn and
/// the transcript becomes the answer. What the recogniser heard is shown, and
/// can be redone, because a recognition error is not the learner's mistake.
///
/// A device that cannot listen — no permission, no recogniser, or the iOS
/// Simulator — falls back to typing. That is worse evidence and the UI says so,
/// but it is far better than a learner who cannot finish the test at all.
class SpokenAnswerField extends ConsumerStatefulWidget {
  const SpokenAnswerField({
    super.key,
    required this.onTranscript,
    required this.transcript,
    required this.enabled,
  });

  /// Called with what the learner said, or with typed text on a device that
  /// cannot listen.
  final ValueChanged<String> onTranscript;

  final String transcript;
  final bool enabled;

  @override
  ConsumerState<SpokenAnswerField> createState() => _SpokenAnswerFieldState();
}

class _SpokenAnswerFieldState extends ConsumerState<SpokenAnswerField> {
  final _typed = TextEditingController();

  bool _listening = false;
  bool? _micAvailable;

  /// The learner asked to type instead. Offered even when the recogniser says
  /// it is available: "available" only means it initialised, and a microphone
  /// that hears nothing — a simulator, a muted headset, a noisy room — leaves
  /// the learner with a working button and no way to answer.
  bool _typing = false;

  /// Held rather than looked up in `dispose`.
  ///
  /// `ref.read` is illegal once the widget is disposed, and calling it there
  /// throws *while the tree is being finalised* — which aborts the frame and
  /// takes any navigation happening at that moment down with it. Signing out
  /// after a placement test did nothing at all for exactly this reason.
  SpeechRecognitionService? _recognition;

  @override
  void initState() {
    super.initState();
    // Asked here rather than at app start, so the permission prompt appears
    // with the microphone visible on screen and its purpose obvious.
    unawaited(_checkMicrophone());
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _recognition = ref.read(speechRecognitionProvider);
  }

  @override
  void dispose() {
    _recognition?.cancel().ignore();
    _typed.dispose();
    super.dispose();
  }

  Future<void> _checkMicrophone() async {
    final available = await ref.read(speechRecognitionProvider).initialise();
    if (mounted) setState(() => _micAvailable = available);
  }

  /// Opens the microphone and leaves it open (§17).
  ///
  /// Push-to-talk: the learner talks for as long as they need and taps again
  /// when they are finished. Ending on silence cut people off mid-answer —
  /// someone composing a sentence in a foreign language pauses constantly, and
  /// a placement answer is exactly where that happens most.
  Future<void> _startListening() async {
    if (!widget.enabled || _listening) return;

    setState(() => _listening = true);
    final started = await _recognition!.startListening(
      onPartial: (heard) {
        // Shown live, so a long pause never looks like a dead microphone.
        if (mounted) widget.onTranscript(heard);
      },
    );

    if (!mounted) return;
    if (!started) setState(() => _listening = false);
  }

  Future<void> _stopListening() async {
    if (!_listening) return;

    final heard = await _recognition!.stopAndRead();
    if (!mounted) return;

    setState(() => _listening = false);
    // Nothing usable: the prompt stands and the learner can try again. An empty
    // transcript must never be submitted as an answer.
    if (heard != null && heard.trim().isNotEmpty) widget.onTranscript(heard);
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);

    if (_micAvailable == false || _typing) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // The notice belongs to a device that cannot listen. A learner who
          // simply chose to type does not need to be told anything is wrong.
          if (_micAvailable == false) ...[
            AppCard(
              color: context.palette.warningSurface,
              borderColor: context.palette.warning.withValues(alpha: 0.35),
              child: Row(
                children: [
                  Icon(Icons.mic_off_rounded,
                      size: 18, color: context.palette.warning),
                  const SizedBox(width: AppSpacing.xs),
                  Expanded(
                    child: Text(s.micUnavailableType,
                        style: context.text.bodySmall),
                  ),
                ],
              ),
            ),
            const SizedBox(height: AppSpacing.sm),
          ],
          TextField(
            controller: _typed,
            maxLines: 4,
            enabled: widget.enabled,
            decoration: InputDecoration(hintText: s.writeYourAnswer),
            onChanged: widget.onTranscript,
          ),
          if (_micAvailable == true) ...[
            const SizedBox(height: AppSpacing.xs),
            Align(
              alignment: AlignmentDirectional.centerStart,
              child: TextButton.icon(
                onPressed: () => setState(() => _typing = false),
                icon: const Icon(Icons.mic_rounded, size: 18),
                label: Text(s.useVoice),
              ),
            ),
          ],
        ],
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Center(
          child: Column(
            children: [
              _MicButton(
                listening: _listening,
                enabled: widget.enabled && _micAvailable != null,
                onTap: _listening ? _stopListening : _startListening,
              ),
              const SizedBox(height: AppSpacing.sm),
              Text(
                _listening
                    ? s.tapWhenDone
                    : widget.transcript.isEmpty
                        ? s.tapThenSpeak
                        : s.speakAgain,
                style: context.text.bodyMedium?.copyWith(
                  color: context.colors.onSurface.withValues(alpha: 0.7),
                ),
              ),
              // Always available. Speaking is measured from what the learner
              // says, and a spoken answer is the better evidence — but no
              // learner should ever be unable to answer at all (§17).
              if (!_listening)
                TextButton(
                  onPressed: () => setState(() => _typing = true),
                  child: Text(s.typeInstead),
                ),
            ],
          ),
        ),
        if (widget.transcript.isNotEmpty) ...[
          const SizedBox(height: AppSpacing.md),
          AppCard(
            color: context.palette.subtleSurface,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(s.yourAnswer, style: context.text.labelMedium),
                const SizedBox(height: AppSpacing.xxs),
                // Shown so the learner can see what was heard and redo it —
                // a recogniser slip is not their mistake to be graded on.
                Text(widget.transcript, style: context.text.bodyLarge),
              ],
            ),
          ),
        ],
      ],
    );
  }
}

class _MicButton extends StatelessWidget {
  const _MicButton({
    required this.listening,
    required this.enabled,
    required this.onTap,
  });

  final bool listening;
  final bool enabled;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final color = listening ? context.palette.danger : context.colors.primary;

    return Semantics(
      button: true,
      label: 'microphone',
      child: GestureDetector(
        onTap: enabled ? onTap : null,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 220),
          width: 96,
          height: 96,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            color: enabled
                ? color.withValues(alpha: listening ? 0.22 : 0.12)
                : context.palette.subtleSurface,
            border: Border.all(
              color: enabled ? color : context.palette.border,
              width: listening ? 3 : 1.5,
            ),
          ),
          child: Icon(
            listening ? Icons.graphic_eq_rounded : Icons.mic_rounded,
            size: 40,
            color: enabled ? color : context.colors.onSurface,
          ),
        ),
      ),
    );
  }
}
