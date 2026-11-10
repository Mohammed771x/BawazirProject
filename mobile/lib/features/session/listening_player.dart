import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/audio/speech_service.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/analytics.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/app_widgets.dart';
import 'clip_playback.dart';

/// The listening clip, with a bar the learner can drag.
///
/// Text-to-speech has no notion of a playhead: it is handed a string and it
/// talks until it runs out. So the clip is spoken one sentence at a time and
/// the sentence boundaries *are* the seek points — dragging picks the sentence
/// containing that point and starts again from there. That is what makes going
/// back to a line you missed, jumping to the end, or switching to the slow
/// voice without losing your place possible at all (ADR-068).
///
/// The bar is drawn against characters rather than sentences so it moves like
/// a media scrubber instead of in visible jumps; only where it *lands* is
/// sentence-granular.
///
/// One widget for the clip before the questions *and* the recording handed
/// back with the result. The result used to have a player of its own — play,
/// stop, nothing else — written before this one could pause on a word, seek
/// or jump, and it stayed that way after this one learnt to: the learner who
/// had just seen which questions they missed got the one player that could
/// not go back to the line they missed.
class ListeningPlayer extends ConsumerStatefulWidget {
  const ListeningPlayer({
    super.key,
    required this.text,
    required this.color,
    this.onAudio,
  });

  final String text;
  final Color color;

  /// Reports play, replay, pause and completion for the admin area (ADR-125).
  final void Function(String event)? onAudio;

  @override
  ConsumerState<ListeningPlayer> createState() => _ListeningPlayerState();
}

class _ListeningPlayerState extends ConsumerState<ListeningPlayer> {
  /// The engine, shared with every other player in the app
  /// (`clip_playback.dart`). This widget owns only the layout.
  ///
  /// Not `final`: a clip regenerated on this screen — the learner changes the
  /// level and the passage is written again — arrives as a new [text] on this
  /// same State. See [didUpdateWidget].
  late ClipPlayback _clip;

  /// Where the learner's finger is while dragging, in characters. The bar
  /// follows the finger; the audio does not move until they let go.
  double? _scrubbing;

  @override
  void initState() {
    super.initState();
    _clip = _newClip();
  }

  // `ref.read` here rather than in a `late final` initialiser: a lazy
  // initialiser runs on first use, and the controller's own teardown is one
  // of the uses — by which time reading a provider throws (ADR-080).
  ClipPlayback _newClip() => ClipPlayback(
    speech: ref.read(speechServiceProvider),
    text: widget.text,
    idPrefix: 'listening',
    prepareEarly: true,
  )..addListener(_repaint);

  @override
  void didUpdateWidget(ListeningPlayer oldWidget) {
    super.didUpdateWidget(oldWidget);
    // The same trap the sentence player fell into: Flutter keeps this State
    // and swaps the text under it, so a controller built once in `initState`
    // would go on speaking the clip the learner just left behind — here, the
    // passage from the level they abandoned, complete with its own scrubber
    // length and clock (ADR-086).
    if (widget.text != oldWidget.text) {
      _clip.removeListener(_repaint);
      _clip.dispose();
      _scrubbing = null;
      _clip = _newClip();
    }
  }

  // Deliberately no auto-play. The clip used to start by itself the moment the
  // screen appeared (§22), which read as the app talking over the learner:
  // they arrive mid-load, the title they were meant to read goes past, and the
  // first thing they do is hunt for the control that makes it stop. The title
  // is the point of the pause — it names what is coming, exactly as an exam
  // prints it above the audio — and the clip begins when they say so (ADR-080).

  /// Whether the clip has been started at all, and whether it had reached
  /// its end — the difference between a play, a replay and a completion.
  bool _everPlayed = false;
  bool _wasFinished = false;

  void _repaint() {
    if (_clip.finished && !_wasFinished) widget.onAudio?.call(ClientEvents.audioCompleted);
    _wasFinished = _clip.finished;
    if (mounted) setState(() {});
  }

  void _toggle() {
    final report = widget.onAudio;
    if (report != null) {
      if (_clip.isPlaying) {
        report(ClientEvents.audioPaused);
      } else if (!_everPlayed) {
        report(ClientEvents.audioPlayed);
      } else if (_clip.finished || _clip.position == 0) {
        report(ClientEvents.audioReplayed);
      }
    }
    _everPlayed = true;
    unawaited(_clip.toggle());
  }

  @override
  void dispose() {
    _clip.removeListener(_repaint);
    // Silences the voice as it goes: nothing may keep talking once this screen
    // is gone, least of all a transcript that is the answer key.
    _clip.dispose();
    super.dispose();
  }

  double get _position => _scrubbing ?? _clip.position;

  /// The clock at a point on the bar — so dragging shows where the finger is
  /// going rather than where the audio still is.
  String _clockAt(double chars) => formatClipTime(_clip.timeAt(chars));

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    // From the controller, which follows the voice itself: when the clip ends
    // on its own the control has to change face without being told.
    final playing = _clip.isPlaying;

    return AppCard(
      color: widget.color.withValues(alpha: 0.06),
      borderColor: widget.color.withValues(alpha: 0.3),
      padding: const EdgeInsets.symmetric(
        horizontal: AppSpacing.md,
        vertical: AppSpacing.lg,
      ),
      child: Column(
        children: [
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              IconButton(
                iconSize: 30,
                color: widget.color,
                tooltip: s.jumpToStart,
                onPressed: () {
                  // Back to the top is a replay in all but name.
                  if (_everPlayed) widget.onAudio?.call(ClientEvents.audioReplayed);
                  unawaited(_clip.seekTo(0));
                },
                icon: const Icon(Icons.first_page_rounded),
              ),
              const SizedBox(width: AppSpacing.sm),
              Container(
                width: 84,
                height: 84,
                decoration: BoxDecoration(
                  color: widget.color.withValues(alpha: 0.14),
                  shape: BoxShape.circle,
                ),
                child: IconButton(
                  iconSize: 40,
                  color: widget.color,
                  onPressed: _toggle,
                  // Three faces, and each one states what the *next* tap does.
                  //
                  // Pausing used to leave a replay face on the button, which
                  // promised the wrong thing: the learner who stopped to think
                  // about a line was told the only way back was from the top.
                  // A paused clip therefore shows the ordinary play triangle,
                  // because the next tap continues (ADR-080). Only a clip that
                  // has actually reached its end offers to replay it.
                  icon: Icon(
                    playing
                        ? Icons.pause_rounded
                        : _clip.finished
                        ? Icons.replay_rounded
                        : Icons.play_arrow_rounded,
                  ),
                ),
              ),
              const SizedBox(width: AppSpacing.sm),
              IconButton(
                iconSize: 30,
                color: widget.color,
                tooltip: s.jumpToEnd,
                onPressed: () =>
                    unawaited(_clip.seekTo(_clip.totalChars.toDouble())),
                icon: const Icon(Icons.last_page_rounded),
              ),
            ],
          ),
          const SizedBox(height: AppSpacing.sm),
          // The scrubber, word-granular where it lands (ADR-082).
          //
          // The unplayed part is drawn in `trackRest` rather than the theme's
          // faint default: this line is the only thing telling the learner
          // where the clip starts and ends, and at border strength it vanished
          // into the card.
          SliderTheme(
            data: SliderTheme.of(context).copyWith(
              trackHeight: 6,
              activeTrackColor: widget.color,
              inactiveTrackColor: context.palette.trackRest,
              thumbColor: widget.color,
              overlayShape: const RoundSliderOverlayShape(overlayRadius: 16),
            ),
            child: Slider(
              value: _position.clamp(0, _clip.totalChars.toDouble()),
              max: _clip.totalChars.toDouble(),
              onChanged: (value) => setState(() => _scrubbing = value),
              onChangeEnd: (value) {
                setState(() => _scrubbing = null);
                unawaited(_clip.seekTo(value));
              },
            ),
          ),
          // How far in, and how long altogether. Both estimated — see
          // `clipClock` — and both read off the same playhead as the bar, so
          // the two can never disagree.
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: AppSpacing.sm),
            child: Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text(
                  _clockAt(_position),
                  style: context.text.labelMedium?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.7),
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
                Text(
                  formatClipTime(_clip.total),
                  style: context.text.labelMedium?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.7),
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.sm),
          // The word under the control names the **state**, not the next tap.
          // It used to name the action, so a learner who had just pressed
          // pause read "Continue" and took the clip to be running (ADR-082).
          Text(
            playing
                ? s.audioPlaying
                : _clip.finished
                ? s.audioFinished
                : _clip.started
                ? s.audioPaused
                : s.playAudio,
            style: context.text.titleSmall,
          ),
          const SizedBox(height: AppSpacing.md),
          SegmentedButton<bool>(
            segments: [
              ButtonSegment(value: false, label: Text(s.normalSpeed)),
              ButtonSegment(value: true, label: Text(s.slowSpeed)),
            ],
            selected: {_clip.slow},
            // Carries on from the sentence being spoken rather than starting
            // the clip again — the controller's rule, not this screen's.
            onSelectionChanged: (value) => _clip.setSlow(value.first),
          ),
          // A device that cannot speak must not block the whole session: the
          // transcript is revealed early rather than after the test
          // (demo review §51).
          if (_clip.audioFailed) ...[
            const SizedBox(height: AppSpacing.md),
            AppCard(
              color: context.palette.warningSurface,
              borderColor: context.palette.warning.withValues(alpha: 0.35),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Icon(
                        Icons.volume_off_rounded,
                        size: 18,
                        color: context.palette.warning,
                      ),
                      const SizedBox(width: AppSpacing.xs),
                      Expanded(
                        child: Text(
                          s.audioUnavailable,
                          style: context.text.labelMedium,
                        ),
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
